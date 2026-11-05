@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
 echo ERROR: .NET Framework C# compiler not found.
 pause
 exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /out:"GIOVI.GG.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll /r:System.Security.dll "GIOVI.GG.cs"
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
start "" "%~dp0GIOVI.GG.exe"
