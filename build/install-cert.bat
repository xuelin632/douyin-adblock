@echo off
cd /d "%~dp0"
"%~dp0python\python.exe" -B "%~dp0install_cert.py"
if errorlevel 1 echo Certificate installation failed. Please check the message above.
pause
