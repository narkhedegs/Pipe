[![Build status](https://ci.appveyor.com/api/projects/status/09u2w3jv56qv2suj?svg=true)](https://ci.appveyor.com/project/narkhedegs/pipe)
# Pipe

Pipe streams in .NET without deadlocks.

<h1 align="center">
  <br>
  <img width="300" src="https://raw.githubusercontent.com/narkhedegs/Pipe/develop/pipe.png">
  <br>
  <br>
</h1>

### Purpose

Pipe helps to "pipe" the streams in .NET without deadlocks.

### Requirements

- .NET 4.5 and above

# Installation

Pipe is available at [Nuget](https://www.nuget.org/packages/Pipe/) and can be installed as a package using VisualStudio NuGet package manager or via the NuGet command line:
> Install-Package Pipe

### Usage

```cs
using Narkhedegs;
```

```cs
var pipe = new Pipe();

// StreamReader could be output of any process. for ex. StandardOutput of System.Diagnostics.Process 
var processStream = streamReader.BaseStream; 

try
{
	int bytesRead;
    
	while(bytesRead = await processStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
    {
    	await pipe.InputStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
    }
}
finally
{
	processStream.Close();
    pipe.InputStream.Close();
}
```

### Credits

All credits goes to [madelson](https://github.com/madelson). This project is just a small part of [MedallionShell](https://github.com/madelson/MedallionShell) published as a separate NuGet package.

### License

MIT © [narkhedegs](https://github.com/narkhedegs)