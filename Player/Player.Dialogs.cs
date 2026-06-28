using System.Diagnostics;

namespace CSharpFFmpeg;

public sealed partial class Player
{
    private static string? OpenFileDialog()
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "zenity", UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("--file-selection");
            psi.ArgumentList.Add("--title=Open Media");
            psi.ArgumentList.Add("--file-filter=All files|*");
            psi.ArgumentList.Add("--file-filter=Video|*.mp4 *.mkv *.avi *.mov *.webm *.flv *.wmv *.mpg *.mpeg *.m4v *.ts *.m2ts *.m3u8 *.vob *.ogv *.3gp *.rm *.rmvb *.asf *.f4v *.dv");
            psi.ArgumentList.Add("--file-filter=Audio|*.mp3 *.aac *.flac *.wav *.ogg *.opus *.m4a *.wma *.ac3 *.dts *.amr *.aiff *.alac");
            var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                return output;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"File dialog error: {ex.Message}");
        }
        return null;
    }

    private static List<string>? OpenFileDialogMultiple()
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "zenity", UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("--file-selection");
            psi.ArgumentList.Add("--multiple");
            psi.ArgumentList.Add("--separator=|");
            psi.ArgumentList.Add("--title=Add Media Files");
            psi.ArgumentList.Add("--file-filter=All files|*");
            psi.ArgumentList.Add("--file-filter=Video|*.mp4 *.mkv *.avi *.mov *.webm *.flv *.wmv *.mpg *.mpeg *.m4v *.ts *.m2ts *.m3u8 *.vob *.ogv *.3gp *.rm *.rmvb *.asf *.f4v *.dv");
            psi.ArgumentList.Add("--file-filter=Audio|*.mp3 *.aac *.flac *.wav *.ogg *.opus *.m4a *.wma *.ac3 *.dts *.amr *.aiff *.alac");
            var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                return output.Split('|').Where(f => !string.IsNullOrEmpty(f)).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"File dialog error: {ex.Message}");
        }
        return null;
    }

    private static string? OpenFolderDialog()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "zenity",
                Arguments = "--file-selection --directory --title='Add Media Folder'",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                return output;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Folder dialog error: {ex.Message}");
        }
        return null;
    }

    private static string? OpenUrlDialog()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "zenity",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--text-info");
            psi.ArgumentList.Add("--title=Wklej URL-e (HLS lub inne)");
            psi.ArgumentList.Add("--editable");
            psi.ArgumentList.Add("--width=600");
            psi.ArgumentList.Add("--height=400");
            var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(120000);
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                return output;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"URL dialog error: {ex.Message}");
        }
        return null;
    }
}
