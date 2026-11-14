@echo off
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4 derleyicisi bulunamadi.
  pause
  exit /b 1
)
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:MicMeter.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll MicMeter.cs
if errorlevel 1 (
  echo Derleme basarisiz.
  pause
  exit /b 1
)
echo Tamam - MicMeter.exe olusturuldu.
start "" MicMeter.exe
