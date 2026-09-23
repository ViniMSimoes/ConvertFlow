@echo off
echo ===================================================
echo Compilando ConvertFlow e Instalador...
echo ===================================================

set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist %CSC% (
    echo Erro: Compilador C# csc.exe nao encontrado em %CSC%.
    pause
    exit /b 1
)

echo [1/2] Compilando ConvertFlow.exe...
%CSC% /target:winexe /reference:System.Xaml.dll /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll" /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationFramework.dll" /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" /reference:System.Xml.dll /reference:System.Data.dll /out:"ConvertFlow.exe" "Program.cs"

if %ERRORLEVEL% NEQ 0 (
    echo [FALHA] Erro ao compilar ConvertFlow.exe!
    pause
    exit /b %ERRORLEVEL%
)

echo [2/2] Compilando Instalador_ConvertFlow.exe...
%CSC% /target:winexe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /out:"Instalador_ConvertFlow.exe" "Instalador_ConvertFlow.cs"

if %ERRORLEVEL% NEQ 0 (
    echo [FALHA] Erro ao compilar Instalador_ConvertFlow.exe!
    pause
    exit /b %ERRORLEVEL%
)

echo ===================================================
echo [SUCESSO] Compilacao concluida com exito!
echo ===================================================
if "%1" neq "--nopause" pause
