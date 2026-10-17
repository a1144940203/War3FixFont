using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace War3FixFont;

public class SettingsManager
{
    private string _path;

    private readonly string _exe = Assembly.GetExecutingAssembly().GetName().Name;

    public Settings Settings { get; private set; }

    public void Load(string path = null)
    {
        // 从快捷方式或提权启动时工作目录可能改变，配置始终与程序放在一起。
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        _path = new FileInfo(path ?? Path.Combine(directory, _exe + ".json")).FullName;
        if (File.Exists(_path))
        {
            var json = File.ReadAllText(_path);
            Settings = JsonConvert.DeserializeObject<Settings>(json);
        }
        else
        {
            Settings = new();
        }
    }

    public void Save()
    {
        var json = JsonConvert.SerializeObject(Settings, Formatting.Indented);
        File.WriteAllText(_path, json, Encoding.UTF8);
    }
}
