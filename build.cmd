@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

REM ===========================================================================
REM  砚湖连 YanhuLink —— 一键构建脚本（绿色版 ZIP + 安装包 EXE）
REM ---------------------------------------------------------------------------
REM  用法：双击运行本文件即可。
REM  产物：
REM    build\YanhuLink-v2.1.0-win-x64-portable.zip        绿色版
REM    installer\Output\YanhuLink-Setup-x64.exe           安装包
REM ===========================================================================

cd /d "%~dp0"

set APP_VERSION=2.1.0
set ISCC=C:\Users\Water\AppData\Local\Programs\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe

echo.
echo ============================================================
echo   砚湖连 YanhuLink v%APP_VERSION% —— 构建开始
echo ============================================================
echo.

REM --- 0. 环境检查 -----------------------------------------------------------
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 dotnet 命令。请先安装 .NET 8 SDK：
    echo        https://dotnet.microsoft.com/download/dotnet/8.0
    pause & exit /b 1
)

if not exist "%ISCC%" (
    echo [警告] 未找到 Inno Setup，将跳过安装包制作。
    echo        安装地址：https://jrsoftware.org/isdl.php
    set SKIP_INSTALLER=1
)

REM --- 1. 清理 --------------------------------------------------------------
echo [1/5] 清理旧产物...
if exist publish\win-x64 rd /s /q publish\win-x64
if exist build\portable   rd /s /q build\portable
if exist installer\Output\*.exe del /q installer\Output\*.exe 2>nul
echo       完成。
echo.

REM --- 2. 发布自包含版 ------------------------------------------------------
echo [2/5] 发布自包含版（含 .NET 与 Windows App Runtime）...
echo       这一步较慢，请耐心等待...
dotnet publish src\CampusNetLogin\CampusNetLogin.csproj ^
    -c Release -r win-x64 -p:Platform=x64 ^
    --self-contained true ^
    -p:WindowsAppSDKSelfContained=true ^
    -p:PublishSingleFile=false ^
    -p:DebugType=none ^
    -o publish\win-x64
if errorlevel 1 (
    echo [错误] 发布失败，请检查上方输出。
    pause & exit /b 1
)
echo       完成。
echo.

REM --- 3. 准备绿色版目录 ----------------------------------------------------
echo [3/5] 准备绿色版目录...
xcopy publish\win-x64 build\portable /E /I /Q /Y >nul
if errorlevel 1 (
    echo [错误] 复制失败。
    pause & exit /b 1
)

REM 精简卫星语言包，只留中英
for /d %%D in (build\portable\*-*) do (
    if /i not "%%~nxD"=="zh-CN" if /i not "%%~nxD"=="zh-TW" if /i not "%%~nxD"=="en-US" (
        rd /s /q "%%D"
    )
)
del /q build\portable\*.pdb 2>nul
copy /y README.md build\portable\ >nul 2>nul
copy /y LICENSE   build\portable\ >nul 2>nul
echo       完成。
echo.

REM --- 4. 打包绿色版 ZIP ----------------------------------------------------
echo [4/5] 打包绿色版 ZIP...
if exist "build\YanhuLink-v%APP_VERSION%-win-x64-portable.zip" del /q "build\YanhuLink-v%APP_VERSION%-win-x64-portable.zip"
powershell -NoProfile -Command ^
  "Compress-Archive -Path 'build\portable\*' -DestinationPath 'build\YanhuLink-v%APP_VERSION%-win-x64-portable.zip' -CompressionLevel Optimal -Force"
if errorlevel 1 (
    echo [错误] 打包失败。
    pause & exit /b 1
)
echo       完成。
echo.

REM --- 5. 生成安装包 --------------------------------------------------------
if defined SKIP_INSTALLER goto :skip_installer
echo [5/5] 生成安装包...
"%ISCC%" installer\setup.iss
if errorlevel 1 (
    echo [错误] 安装包编译失败。
    pause & exit /b 1
)
echo       完成。
goto :report

:skip_installer
echo [5/5] 已跳过安装包（未安装 Inno Setup）。
echo.

:report
echo.
echo ============================================================
echo   构建完成！产物如下：
echo ============================================================
for %%F in ("build\YanhuLink-v%APP_VERSION%-win-x64-portable.zip") do (
    if exist "%%F" echo   [绿色版] %%F   (%%~zF 字节)
)
if not defined SKIP_INSTALLER (
    for %%F in ("installer\Output\YanhuLink-Setup-x64.exe") do (
        if exist "%%F" echo   [安装包] %%F   (%%~zF 字节)
    )
)
echo.
echo   下一步：
echo     - 绿色版：解压后双击 CampusNetLogin.exe
echo     - 安装包：双击运行，按向导安装
echo     - 发布到 GitHub：先改 git tag，再 git push --tags
echo.
pause
