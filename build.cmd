set USERPROFILE=C:\Users\lsv
@ECHO OFF
PowerShell -NoProfile -NoLogo -ExecutionPolicy unrestricted -Command "[System.Threading.Thread]::CurrentThread.CurrentCulture = ''; [System.Threading.Thread]::CurrentThread.CurrentUICulture = '';& '%~dp0run.ps1' default-build %*; exit $LASTEXITCODE"



:"C:\Users\Sergii Lutai\.dotnet\x64" 