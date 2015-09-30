#addin "Cake.Powershell"

///////////////////////////////////////////////////////////////////////////////
// ARGUMENTS
///////////////////////////////////////////////////////////////////////////////

var target = Argument<string>("target", "Default");
var configuration = Argument<string>("configuration", "Release");

///////////////////////////////////////////////////////////////////////////////
// PREPARATION
///////////////////////////////////////////////////////////////////////////////

// Get whether or not this is a local build.
var local = BuildSystem.IsLocalBuild;
var isRunningOnAppVeyor = AppVeyor.IsRunningOnAppVeyor;
var isPullRequest = AppVeyor.Environment.PullRequest.IsPullRequest;

// Parse release notes.
var releaseNotes = ParseReleaseNotes("./ReleaseNotes.md");

// Get version.
var buildNumber = AppVeyor.Environment.Build.Number;
var version = releaseNotes.Version.ToString();
var semanticVersion = (local || target == "Publish") ? version : (version + string.Concat("-build-", buildNumber));

// Define directories.
var toolsDirectory = Directory("./Tools");
var sourceDirectory = Directory("./Source");
var outputDirectory = Directory("./Output");
var temporaryDirectory = Directory("./Temporary");
var testResultsDirectory = outputDirectory + Directory("TestResults");
var artifactsDirectory = outputDirectory + Directory("Artifacts");
var solutions = GetFiles("./**/*.sln");
var solutionPaths = solutions.Select(solution => solution.GetDirectory());

// Define files.
var nugetExecutable = "./Tools/nuget.exe"; 

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(() =>
{
    // Executed BEFORE the first task.
    Information("Target: " + target);
    Information("Configuration: " + configuration);
    Information("Is local build: " + local.ToString());
    Information("Is running on AppVeyor: " + isRunningOnAppVeyor.ToString());
    Information("Semantic Version: " + semanticVersion);
});

Teardown(() =>
{
    // Executed AFTER the last task.
    Information("Finished running tasks.");
});

///////////////////////////////////////////////////////////////////////////////
// TASK DEFINITIONS
///////////////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() =>
{
    // Clean solution directories.
    foreach(var path in solutionPaths)
    {
        Information("Cleaning {0}", path);
        CleanDirectories(path + "/**/bin/" + configuration);
        CleanDirectories(path + "/**/obj/" + configuration);
    }

	CleanDirectories(outputDirectory);
	CleanDirectories(temporaryDirectory);
});

Task("Create-Directories")
	.IsDependentOn("Clean")
    .Does(() =>
{
	var directories = new List<DirectoryPath>{ outputDirectory, testResultsDirectory, artifactsDirectory, temporaryDirectory };
	directories.ForEach(directory => 
	{
		if (!DirectoryExists(directory))
		{
			CreateDirectory(directory);
		}
	});
});

Task("Install-Framework-Manager")
	.IsDependentOn("Create-Directories")
	.WithCriteria(() => !local)
    .Does(() =>
{
	StartPowershellScript("iex ((new-object net.webclient).DownloadString('https://raw.githubusercontent.com/aspnet/Home/dev/dnvminstall.ps1'))");
});

Task("Install-Framework")
	.IsDependentOn("Install-Framework-Manager")
	.WithCriteria(() => !local)
    .Does(() =>
{
	StartPowershellScript(@"
		$env:DNX_HOME = ""C:\Users\appveyor\.dnx""
		$env:DNX_GLOBAL_HOME = ""C:\Users\appveyor\.dnx""
		$GlobalJson = Get-Content -Raw -Path global.json | ConvertFrom-Json
		dnvm install $GlobalJson.sdk.version -r $GlobalJson.sdk.runtime -arch $GlobalJson.sdk.architecture");
});

Task("Set-Framework")
	.IsDependentOn("Install-Framework")
    .Does(() =>
{
	StartPowershellScript(@"
		$GlobalJson = Get-Content -Raw -Path global.json | ConvertFrom-Json
		dnvm use $GlobalJson.sdk.version -r $GlobalJson.sdk.runtime -arch $GlobalJson.sdk.architecture");
});

Task("Restore-NuGet-Packages")
	.IsDependentOn("Set-Framework")
    .Does(() =>
{
	StartPowershellScript("dnu.cmd restore");
});


Task("Patch-Project-Json")
    .IsDependentOn("Restore-NuGet-Packages")
	.WithCriteria(() => !local)
    .Does(() =>
{
	var projectJsonFiles = GetFiles("./**/project.json");
	foreach(var projectJsonFile in projectJsonFiles)
	{
		var projectJson = System.IO.File.ReadAllText(projectJsonFile.GetDirectory() + "/project.json");
		projectJson = projectJson.Replace("\"version\": \"0.0.0\"","\"version\": \"" + semanticVersion + "\"");
		System.IO.File.WriteAllText(projectJsonFile.GetDirectory() + "/project.json", projectJson);
	}
});

Task("Build")
    .IsDependentOn("Patch-Project-Json")
    .Does(() =>
{
	var projectJsonFiles = GetFiles("./**/project.json");
	foreach(var projectJsonFile in projectJsonFiles)
	{
		StartPowershellScript("dnu.cmd build " + projectJsonFile + " --configuration " + configuration);
	}
});

Task("Copy-Files")
    .IsDependentOn("Build")
    .Does(() =>
{
    CopyFiles(GetFiles("./Source/**/*.dll"), temporaryDirectory);
	CopyFiles(GetFiles("./Tests/**/*.dll"), temporaryDirectory);
	CopyFiles(GetFiles("./Libraries/NuGetPackages/NUnit/**/*.dll"), temporaryDirectory);
});

Task("Run-Unit-Tests")
    .IsDependentOn("Copy-Files")
    .Does(() =>
{
	var testAssemblies = GetFiles("./Temporary/*.Tests.dll");
	if(testAssemblies.Count() > 0)
	{
	    NUnit("./Temporary/*.Tests.dll", 
		new NUnitSettings 
			{ 
				OutputFile = testResultsDirectory.Path + "/TestResults.xml", 
				NoResults = true 
			}
		);
	}
});

Task("Create-NuGet-Packages")
    .IsDependentOn("Run-Unit-Tests")
    .Does(() =>
{
	var projectJsonFiles = GetFiles("./Source/**/project.json");
	foreach(var projectJsonFile in projectJsonFiles)
	{
		StartPowershellScript("dnu.cmd pack " + projectJsonFile  + " --configuration " + configuration + " --out " + artifactsDirectory.Path);
	}
});

Task("Update-AppVeyor-Build-Number")
    .WithCriteria(() => isRunningOnAppVeyor)
    .Does(() =>
{
    AppVeyor.UpdateBuildVersion(semanticVersion);
});

Task("Upload-AppVeyor-Artifacts")
    .IsDependentOn("Create-NuGet-Packages")
    .WithCriteria(() => isRunningOnAppVeyor)
    .Does(() =>
{
	var artifacts = GetFiles(artifactsDirectory.Path + "/**/*.nupkg");
	foreach(var artifact in artifacts)
	{
		AppVeyor.UploadArtifact(artifact);
	}
});

Task("Publish-NuGet-Packages")
	.IsDependentOn("Upload-AppVeyor-Artifacts")
    .WithCriteria(() => !local)
    .WithCriteria(() => !isPullRequest)
    .Does(() =>
{
    // Resolve the API key.
    var apiKey = EnvironmentVariable("NuGetApiKey");
    if(string.IsNullOrEmpty(apiKey)) {
        throw new InvalidOperationException("Could not resolve NuGet API key.");
    }

	var nugetPackages = GetFiles(artifactsDirectory.Path + "/**/*.nupkg");
	foreach(var nugetPackage in nugetPackages)
	{
		NuGetPush(nugetPackage, new NuGetPushSettings {
			ApiKey = apiKey
		});
	}
});

///////////////////////////////////////////////////////////////////////////////
// TARGETS
///////////////////////////////////////////////////////////////////////////////

Task("Default")
    .IsDependentOn("Run-Unit-Tests");

Task("Package")
	.IsDependentOn("Update-AppVeyor-Build-Number")
	.IsDependentOn("Upload-AppVeyor-Artifacts");

Task("Publish")
	.IsDependentOn("Update-AppVeyor-Build-Number")
	.IsDependentOn("Upload-AppVeyor-Artifacts")
    .IsDependentOn("Publish-NuGet-Packages");

///////////////////////////////////////////////////////////////////////////////
// EXECUTION
///////////////////////////////////////////////////////////////////////////////

RunTarget(target);