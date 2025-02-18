using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace XIVLauncherMigrator;

public partial class MainWindow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private const string CNRelativePath     = @"AppData\Roaming\XIVLauncherCN";
    private const string GlobalRelativePath = @"AppData\Roaming\XIVLauncher";

    // 改为可设置的属性
    private string _sourcePath;

    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            _sourcePath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourcePath)));
        }
    }

    private string _targetPath;

    public string TargetPath
    {
        get => _targetPath;
        set
        {
            _targetPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetPath)));
        }
    }

    public MainWindow()
    {
        SourcePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            CNRelativePath);

        InitializeComponent();
        DataContext = this;
        CheckPrerequisites();
    }


    private void RadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: not null } radioButton) return;

        var relativePath = radioButton.Tag.ToString() == "CN" ? CNRelativePath : GlobalRelativePath;
        SourcePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            relativePath);
    }

    private void CheckPrerequisites()
    {
        if (Directory.Exists(SourcePath)) return;

        MessageBox.Show("原目录不存在，可能未安装 XIVLauncher", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
        Close();
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new CommonOpenFileDialog();
        dialog.IsFolderPicker = true;
        if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;

        // 根据当前选择的服务器类型确定目标文件夹名称
        var folderName = SourcePath.Contains("XIVLauncherCN") ? "XIVLauncherCN" : "XIVLauncher";
        TargetPath = Path.Combine(dialog.FileName, folderName);
    }

    private async void BtnMigrate_Click(object sender, RoutedEventArgs e)
    {
        var backupPath = string.Empty;
        try
        {
            lblStatus.Text = "正在检查文件锁定状态...";
            if (!await CheckFilesNotLocked(SourcePath))
            {
                MessageBox.Show("检测到部分文件正在被其他程序使用，请关闭相关程序后重试", "警告",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                lblStatus.Text = "就绪";
                return;
            }

            lblStatus.Text = "正在验证路径...";
            if (string.IsNullOrWhiteSpace(TargetPath))
            {
                MessageBox.Show("请先选择目标路径", "提示",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                lblStatus.Text = "就绪";
                return;
            }

            if (Directory.Exists(TargetPath))
            {
                MessageBox.Show("目标目录已存在，请选择其他路径", "错误",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "就绪";
                return;
            }

            // 创建备份目录用于回滚
            backupPath = Path.Combine(Path.GetTempPath(),
                                      "XIVLauncherCN_Backup_" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            lblStatus.Text = "正在创建备份...";
            await CopyDirectoryCrossVolume(SourcePath, backupPath);

            lblStatus.Text = "正在迁移数据...";
            // 判断是否跨磁盘分区
            var sourceDrive  = Path.GetPathRoot(SourcePath);
            var targetDrive  = Path.GetPathRoot(TargetPath);
            var isSameVolume = string.Equals(sourceDrive, targetDrive, StringComparison.OrdinalIgnoreCase);

            if (isSameVolume)
            {
                // 同一磁盘分区使用快速移动
                await Task.Run(() => Directory.Move(SourcePath, TargetPath));
            }
            else
            {
                // 跨分区使用复制+删除策略
                await CopyDirectoryCrossVolume(SourcePath, TargetPath);

                // 验证文件完整性
                lblStatus.Text = "正在验证文件完整性...";
                if (!await VerifyDirectoryIntegrity(SourcePath, TargetPath)) throw new Exception("文件完整性验证失败");

                await Task.Run(() => Directory.Delete(SourcePath, true));
            }

            lblStatus.Text = "创建符号链接...";
            CreateSymbolicLink(SourcePath, TargetPath);

            MessageBox.Show("迁移成功完成！", "成功",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            lblStatus.Text = "就绪";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"操作失败: {ex.Message}", "错误",
                            MessageBoxButton.OK, MessageBoxImage.Error);

            // 如果有备份，执行回滚
            if (!string.IsNullOrEmpty(backupPath) && Directory.Exists(backupPath))
            {
                try
                {
                    lblStatus.Text = "正在回滚...";
                    if (Directory.Exists(SourcePath))
                        Directory.Delete(SourcePath, true);
                    Directory.Move(backupPath, SourcePath);
                    MessageBox.Show("已成功回滚到原始状态", "提示",
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception rollbackEx)
                {
                    MessageBox.Show($"回滚失败: {rollbackEx.Message}\n原始数据已备份至: {backupPath}", "严重错误",
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            lblStatus.Text = "就绪";
        } finally
        {
            // 清理备份
            if (!string.IsNullOrEmpty(backupPath) && Directory.Exists(backupPath))
            {
                try { Directory.Delete(backupPath, true); }
                catch
                {
                    /* 忽略清理备份时的错误 */
                }
            }
        }
    }

    private async Task CopyDirectoryCrossVolume(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var totalFiles  = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories).Length;
        var copiedFiles = 0;

        await CopyFiles(sourceDir, targetDir);
        return;

        async Task CopyFiles(string currentSourceDir, string currentTargetDir)
        {
            Directory.CreateDirectory(currentTargetDir);

            foreach (var file in Directory.GetFiles(currentSourceDir))
            {
                var destFile = Path.Combine(currentTargetDir, Path.GetFileName(file));
                await Task.Run(() => File.Copy(file, destFile, true));
                copiedFiles++;
                var files = copiedFiles;
                lblStatus.Dispatcher.Invoke(() =>
                                                lblStatus.Text = $"正在复制文件... ({files}/{totalFiles})");
            }

            foreach (var directory in Directory.GetDirectories(currentSourceDir))
            {
                var destDir = Path.Combine(currentTargetDir, Path.GetFileName(directory));
                await CopyFiles(directory, destDir);
            }
        }
    }

    private async Task<bool> VerifyDirectoryIntegrity(string sourceDir, string targetDir)
    {
        var totalFiles    = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories).Length;
        var verifiedFiles = 0;

        return await VerifyFiles(sourceDir, targetDir);

        async Task<bool> VerifyFiles(string currentSourceDir, string currentTargetDir)
        {
            foreach (var sourceFile in Directory.GetFiles(currentSourceDir))
            {
                var fileName   = Path.GetFileName(sourceFile);
                var targetFile = Path.Combine(currentTargetDir, fileName);

                if (!File.Exists(targetFile))
                    return false;

                var sourceHash = await Task.Run(() => CalculateFileHash(sourceFile));
                var targetHash = await Task.Run(() => CalculateFileHash(targetFile));

                if (sourceHash != targetHash)
                    return false;

                verifiedFiles++;
                var files = verifiedFiles;
                lblStatus.Dispatcher.Invoke(() =>
                                                lblStatus.Text = $"正在验证文件... ({files}/{totalFiles})");
            }

            foreach (var sourceSubDir in Directory.GetDirectories(currentSourceDir))
            {
                var dirName      = Path.GetFileName(sourceSubDir);
                var targetSubDir = Path.Combine(currentTargetDir, dirName);

                if (!Directory.Exists(targetSubDir))
                    return false;

                if (!await VerifyFiles(sourceSubDir, targetSubDir))
                    return false;
            }

            return true;
        }
    }

    private static string CalculateFileHash(string filePath)
    {
        using var md5    = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var       hash   = md5.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private async Task<bool> CheckFilesNotLocked(string directory)
    {
        try
        {
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                try
                {
                    await Task.Run(() =>
                    {
                        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
                    });
                }
                catch (IOException) { return false; }

            foreach (var subDir in Directory.GetDirectories(directory))
                if (!await CheckFilesNotLocked(subDir))
                    return false;

            return true;
        }
        catch (Exception) { return false; }
    }

    private static void CreateSymbolicLink(string linkPath, string targetPath)
    {
        // 需要以管理员权限运行
        var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            FileName    = "cmd.exe",
            Arguments   = $"/C mklink /D \"{linkPath}\" \"{targetPath}\"",
            Verb        = "runas"
        };
        process.StartInfo = startInfo;
        process.Start();
        process.WaitForExit();
    }
}
