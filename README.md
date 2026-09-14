
<p align="center">
    <img src="https://github.com/wildonion/deadsouls/blob/master/ds.PNG">
</p>

## Compile

C2Server and DeadsoulsExplorer compile with the plain .NET Framework 4.x compiler (no NuGet):

```shell
# C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

# 1. C2 server
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe /optimize /out:C2Server.exe C2Server.cs /r:System.dll /r:System.Core.dll

# 2. GUI file-explorer (WinForms)
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize /out:DeadsoulsExplorer.exe DeadsoulsExplorer.cs /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll
```

## Run C2 CLI

```shell
./C2Server.exe --selftest
./C2Server.exe --auto
./C2Server.exe --key verysecrethighentropyrandomkey
```

