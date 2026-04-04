using System.Text;
using System.Windows;
using System.IO;

namespace PhoneShell;

public partial class FileSessionWindow : Window
{
    private const int MaxPreviewBytes = 512 * 1024;

    public FileSessionWindow(string filePath)
    {
        InitializeComponent();
        FilePath = filePath;
        Loaded += FileSessionWindow_Loaded;
    }

    public string FilePath { get; }

    public event Action<string>? InsertPathRequested;

    private void FileSessionWindow_Loaded(object sender, RoutedEventArgs e)
    {
        FilePathText.Text = FilePath;
        Title = $"File Session - {System.IO.Path.GetFileName(FilePath)}";
        LoadFilePreview();
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        LoadFilePreview();
    }

    private void InsertPathButton_Click(object sender, RoutedEventArgs e)
    {
        InsertPathRequested?.Invoke(FilePath);
    }

    private void LoadFilePreview()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
        {
            FileContentText.Text = "文件不存在或路径无效。";
            return;
        }

        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var previewBytes = (int)Math.Min(stream.Length, MaxPreviewBytes);
            var buffer = new byte[previewBytes];
            var read = stream.Read(buffer, 0, previewBytes);

            if (read <= 0)
            {
                FileContentText.Text = string.Empty;
                return;
            }

            if (ContainsBinaryData(buffer, read))
            {
                FileContentText.Text = "该文件看起来是二进制文件，暂不支持文本预览。";
                return;
            }

            var preview = Encoding.UTF8.GetString(buffer, 0, read);
            if (stream.Length > MaxPreviewBytes)
            {
                preview += $"\n\n... [预览已截断，仅显示前 {MaxPreviewBytes / 1024} KB]";
            }

            FileContentText.Text = preview;
        }
        catch (Exception ex)
        {
            FileContentText.Text = $"读取文件失败: {ex.Message}";
        }
    }

    private static bool ContainsBinaryData(byte[] buffer, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] == 0)
                return true;
        }

        return false;
    }
}
