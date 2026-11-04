@echo off
rem Builds build\cap-compress.exe with the C# compiler that ships with Windows.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
cd /d "%~dp0"
if not exist build mkdir build
"%CSC%" /nologo /optimize+ /target:winexe /win32icon:src\app.ico /out:build\cap-compress.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:"%FW%\System.IO.Compression.dll" /r:"%FW%\System.IO.Compression.FileSystem.dll" ^
  src\Engine.cs src\Theme.cs src\MainForm.cs
