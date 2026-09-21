using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;

namespace ConvertFlowInstaller
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            DialogResult result = MessageBox.Show(
                "Deseja instalar ou atualizar o ConvertFlow neste computador?\n\n" +
                "• Cria/atualiza atalhos na Área de Trabalho e no Menu Iniciar\n" +
                "• Fecha versões anteriores automaticamente sem travar\n" +
                "• 100% nativo e não requer permissões de administrador",
                "Instalador do ConvertFlow",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result != DialogResult.Yes)
            {
                return;
            }

            try
            {
                // 1. FECHA QUALQUER PROCESSO DO CONVERTFLOW QUE ESTEJA ABERTO
                // Isso evita o erro de 'Arquivo em uso' do Windows que obrigava a deletar a pasta manualmente
                CloseRunningProcesses("ConvertFlow");
                Thread.Sleep(600);

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string targetDir = Path.Combine(appData, "ConvertFlow");

                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                // 2. LOCALIZA O EXECUTÁVEL NATIVO DE ORIGEM
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string sourceExe = Path.Combine(baseDir, "ConvertFlow.exe");
                if (!File.Exists(sourceExe))
                {
                    sourceExe = Path.Combine(baseDir, "ConvertFlow", "ConvertFlow.exe");
                }

                string targetExe = Path.Combine(targetDir, "ConvertFlow.exe");

                if (File.Exists(sourceExe))
                {
                    SafeCopyFile(sourceExe, targetExe);
                }
                else
                {
                    // Se estiver em modo pacote completo, copia a pasta
                    string sourceDir = Directory.Exists(Path.Combine(baseDir, "ConvertFlow")) ? Path.Combine(baseDir, "ConvertFlow") : baseDir;
                    CopyDirectorySafe(sourceDir, targetDir);
                }

                // 3. CRIA ATALHO NA ÁREA DE TRABALHO
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string desktopShortcut = Path.Combine(desktop, "ConvertFlow.lnk");
                CreateShortcut(desktopShortcut, targetExe, targetDir);

                // 4. CRIA ATALHO NO MENU INICIAR
                string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string startMenuShortcut = Path.Combine(startMenu, "ConvertFlow.lnk");
                CreateShortcut(startMenuShortcut, targetExe, targetDir);

                // 5. CRIA SCRIPT DE DESINSTALAÇÃO LIMPO
                string uninstallerPath = Path.Combine(targetDir, "Desinstalar.bat");
                File.WriteAllText(uninstallerPath, string.Format(
                    "@echo off\r\n" +
                    "taskkill /f /im ConvertFlow.exe >nul 2>&1\r\n" +
                    "timeout /t 1 /nobreak >nul\r\n" +
                    "del /f /q \"{0}\"\r\n" +
                    "del /f /q \"{1}\"\r\n" +
                    "cd ..\r\n" +
                    "rmdir /s /q \"{2}\"\r\n" +
                    "echo ConvertFlow desinstalado com sucesso.\r\n" +
                    "pause\r\n",
                    desktopShortcut,
                    startMenuShortcut,
                    targetDir
                ));

                // 6. INICIA O PROGRAMA INSTALADO
                Process.Start(targetExe);

                MessageBox.Show(
                    "ConvertFlow foi instalado/atualizado com sucesso!\n\n" +
                    "O aplicativo já foi aberto e o atalho está pronto na sua Área de Trabalho.",
                    "Instalação Concluída",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro durante a instalação:\n" + ex.Message, "Falha na Instalação", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void CloseRunningProcesses(string processName)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(2000);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void SafeCopyFile(string source, string target)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (File.Exists(target))
                    {
                        File.SetAttributes(target, FileAttributes.Normal);
                    }
                    File.Copy(source, target, true);
                    return;
                }
                catch
                {
                    CloseRunningProcesses("ConvertFlow");
                    Thread.Sleep(500);
                }
            }
        }

        private static void CopyDirectorySafe(string sourceDir, string targetDir)
        {
            foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relative = dir.Substring(sourceDir.Length).TrimStart('\\', '/');
                if (relative.StartsWith(".git") || relative.Equals("build-executable") || relative.Equals("dist-exe"))
                    continue;

                string targetSubDir = Path.Combine(targetDir, relative);
                if (!Directory.Exists(targetSubDir))
                {
                    Directory.CreateDirectory(targetSubDir);
                }
            }

            foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(sourceDir.Length).TrimStart('\\', '/');
                if (relative.StartsWith(".git") || relative.EndsWith(".cs") || relative.Contains("Instalador_ConvertFlow"))
                    continue;

                string targetFile = Path.Combine(targetDir, relative);
                SafeCopyFile(file, targetFile);
            }
        }

        private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType);
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    shortcut.TargetPath = targetPath;
                    shortcut.WorkingDirectory = workingDir;
                    shortcut.Description = "ConvertFlow - Conversor de Arquivos";
                    shortcut.Save();
                }
            }
            catch { }
        }
    }
}
