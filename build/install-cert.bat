@echo off
chcp 65001 >nul
title 安装证书
cd /d "%~dp0"

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo   [请以管理员身份运行]
    echo.
    echo   右键点击本文件 -^> 以管理员身份运行
    echo.
    pause
    exit /b 1
)

echo ========================================
echo    第一步：生成解密证书
echo ========================================
set "CERT=%USERPROFILE%\.mitmproxy\mitmproxy-ca-cert.cer"

if not exist "%CERT%" (
    echo   正在生成...
    start "" /min "%~dp0python\Scripts\mitmdump.exe" --listen-port 18080
    timeout /t 5 /nobreak >nul
    taskkill /F /IM mitmdump.exe >nul 2>&1
    timeout /t 2 /nobreak >nul
)

if not exist "%CERT%" (
    echo   [失败] 证书没有生成，请把本窗口截图反馈
    pause
    exit /b 1
)
echo   已生成: %CERT%
echo.

echo ========================================
echo    第二步：装入系统信任区
echo ========================================
certutil -addstore -user Root "%CERT%" >nul 2>&1
certutil -addstore Root "%CERT%" >nul 2>&1
echo   已装入「当前用户」和「本机」两个信任区
echo.

echo ========================================
echo    完成！
echo ========================================
echo.
echo   以后双击「抖音去广告.exe」就能用了。
echo.
echo   提示：如果后悔了想卸载证书，
echo         在「管理计算机证书 - 受信任的根证书颁发机构」里删掉 mitmproxy 即可。
echo.
pause
