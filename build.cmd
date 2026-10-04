@echo off
rem Builds RedfallSaveTransfer.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio or SDK needed.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find %CSC%
  echo Install .NET Framework 4.8 or build with Visual Studio instead.
  exit /b 1
)
cd /d "%~dp0"
if not exist bin mkdir bin
"%CSC%" /nologo /optimize+ /platform:x64 /target:exe /out:bin\RedfallSaveTransfer.exe src\*.cs
if errorlevel 1 exit /b 1
echo Built bin\RedfallSaveTransfer.exe
