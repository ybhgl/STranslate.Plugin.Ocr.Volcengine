using CommunityToolkit.Mvvm.ComponentModel;
using STranslate.Plugin.Ocr.Volcengine.View;
using STranslate.Plugin.Ocr.Volcengine.ViewModel;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;

namespace STranslate.Plugin.Ocr.Volcengine;

public class Main : ObservableObject, IOcrPlugin, ILlm
{
    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private Settings Settings { get; set; } = null!;
    private IPluginContext Context { get; set; } = null!;

    public IEnumerable<LangEnum> SupportedLanguages => Enum.GetValues<LangEnum>();

    public ObservableCollection<Prompt> Prompts { get; set; } = [];

    public Prompt? SelectedPrompt
    {
        get => Prompts.FirstOrDefault(p => p.IsEnabled);
        set => SelectPrompt(value);
    }

    public void SelectPrompt(Prompt? prompt)
    {
        if (prompt == null) return;

        // 更新所有 Prompt 的 IsEnabled 状态
        foreach (var p in Prompts)
        {
            p.IsEnabled = p == prompt;
        }

        OnPropertyChanged(nameof(SelectedPrompt));

        // 保存到配置
        Settings.Prompts = [.. Prompts.Select(p => p.Clone())];
        Context.SaveSettingStorage<Settings>();
    }

    public Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel(Context, Settings, this);
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    public void Init(IPluginContext context)
    {
        Context = context;
        Settings = context.LoadSettingStorage<Settings>();

        // 加载 Prompt 列表
        Settings.Prompts.ForEach(Prompts.Add);
    }

    public void Dispose() => _viewModel?.Dispose();

    public string? GetLanguage(LangEnum langEnum) => null;

    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        UriBuilder uriBuilder = new(Settings.Url);
        // 如果路径不是有效的API路径结尾，使用默认路径
        if (uriBuilder.Path == "/")
            uriBuilder.Path = "/api/v3/responses";

        var imageQuality = GetImageQualityOrDefault();
        if (imageQuality == ImageQuality.High)
            return new OcrResult().Fail($"Not supported, please use {Context.GetTranslation("ImageQualityLow")} or {Context.GetTranslation("ImageQualityMedium")}");

        // 处理图片数据
        var base64Str = Convert.ToBase64String(request.ImageData);
        var formatStr = imageQuality switch
        {
            ImageQuality.Low => "image/jpeg",
            ImageQuality.Medium => "image/png",
            ImageQuality.High => "image/bmp",
            _ => "image/png"
        };

        // 选择模型
        var model = Settings.Model.Trim();
        model = string.IsNullOrEmpty(model) ? "doubao-seed-1-6-vision-250815" : model;

        // 替换Prompt关键字
        var messages = (Prompts.FirstOrDefault(x => x.IsEnabled) ?? throw new Exception("请先完善Prompt配置"))
            .Clone()
            .Items;
        messages.ToList()
            .ForEach(item =>
                item.Content = item.Content.Replace("$target", ConvertLanguage(request.Language)));

        // 温度限定
        var temperature = Math.Clamp(Settings.Temperature, 0, 2);
        var userPrompt = messages.LastOrDefault() ?? throw new Exception("Prompt配置为空");
        messages.Remove(userPrompt);
        var messages2 = new List<object>();
        foreach (var item in messages)
        {
            messages2.Add(new
            {
                role = item.Role,
                content = new object[]
                {
                    new
                    {
                        type = "input_text",
                        text = item.Content
                    }
                }
            });
        }
        messages2.Add(new
        {
            role = "user",
            content = new object[]
            {
                new
                {
                    type = "input_text",
                    text = userPrompt.Content
                },
                new
                {
                    type = "input_image",
                    image_url = $"data:{formatStr};base64,{base64Str}"
                }
            }
        });

        var content = new
        {
            model,
            input = messages2.ToArray(),
            temperature,
            thinking = new
            {
                type = Settings.Thinking ? "enabled" : "disabled"
            }
        };

        var option = new Options
        {
            Headers = new Dictionary<string, string>
            {
                { "authorization", "Bearer " + Settings.ApiKey }
            }
        };

        var response = await Context.HttpService.PostAsync(uriBuilder.Uri.ToString(), content, option, cancellationToken);
        // 解析Google翻译返回的JSON
        var jsonDoc = JsonDocument.Parse(response);
        var rawData = ExtractResponseText(jsonDoc.RootElement, response);

        var result = new OcrResult();
        foreach (var item in rawData.ToString().Split("\n").ToList().Select(item => new OcrContent { Text = item}))
        {
            result.OcrContents.Add(item);
        }

        return result;
    }

    private static string ExtractResponseText(JsonElement root, string rawResponse)
    {
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            return outputText.GetString() ?? throw new Exception($"反序列化失败: {rawResponse}");

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var outputItem in output.EnumerateArray())
            {
                if (outputItem.TryGetProperty("type", out var outputType) &&
                    outputType.ValueKind == JsonValueKind.String &&
                    outputType.GetString() == "reasoning")
                    continue;

                if (outputItem.TryGetProperty("type", out outputType) &&
                    outputType.ValueKind == JsonValueKind.String &&
                    outputType.GetString() == "message")
                {
                    if (outputItem.TryGetProperty("role", out var roleElement) &&
                        roleElement.ValueKind == JsonValueKind.String &&
                        roleElement.GetString() != "assistant")
                        continue;
                }

                if (!outputItem.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (contentItem.ValueKind != JsonValueKind.Object)
                        continue;

                    if (contentItem.TryGetProperty("type", out var typeElement) &&
                        typeElement.ValueKind == JsonValueKind.String &&
                        typeElement.GetString() == "output_text" &&
                        contentItem.TryGetProperty("text", out var textElement))
                    {
                        builder.Append(textElement.GetString());
                        continue;
                    }

                    if (contentItem.TryGetProperty("text", out var fallbackText) &&
                        fallbackText.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(fallbackText.GetString());
                    }
                }
            }

            if (builder.Length > 0)
                return builder.ToString();
        }

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? throw new Exception($"反序列化失败: {rawResponse}");
            }
        }

        throw new Exception($"反序列化失败: {rawResponse}");
    }

    private ImageQuality GetImageQualityOrDefault()
    {
        var property = Context.GetType().GetProperty("ImageQuality");
        if (property?.PropertyType == typeof(ImageQuality) &&
            property.GetValue(Context) is ImageQuality quality)
        {
            return quality;
        }

        return ImageQuality.Medium;
    }

    private string ConvertLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "Requires you to identify automatically",
        LangEnum.ChineseSimplified => "Simplified Chinese",
        LangEnum.ChineseTraditional => "Traditional Chinese",
        LangEnum.Cantonese => "Cantonese",
        LangEnum.English => "English",
        LangEnum.Japanese => "Japanese",
        LangEnum.Korean => "Korean",
        LangEnum.French => "French",
        LangEnum.Spanish => "Spanish",
        LangEnum.Russian => "Russian",
        LangEnum.German => "German",
        LangEnum.Italian => "Italian",
        LangEnum.Turkish => "Turkish",
        LangEnum.PortuguesePortugal => "Portuguese",
        LangEnum.PortugueseBrazil => "Portuguese",
        LangEnum.Vietnamese => "Vietnamese",
        LangEnum.Indonesian => "Indonesian",
        LangEnum.Thai => "Thai",
        LangEnum.Malay => "Malay",
        LangEnum.Arabic => "Arabic",
        LangEnum.Hindi => "Hindi",
        LangEnum.MongolianCyrillic => "Mongolian",
        LangEnum.MongolianTraditional => "Mongolian",
        LangEnum.Khmer => "Central Khmer",
        LangEnum.NorwegianBokmal => "Norwegian Bokmål",
        LangEnum.NorwegianNynorsk => "Norwegian Nynorsk",
        LangEnum.Persian => "Persian",
        LangEnum.Swedish => "Swedish",
        LangEnum.Polish => "Polish",
        LangEnum.Dutch => "Dutch",
        LangEnum.Ukrainian => "Ukrainian",
        _ => "Requires you to identify automatically"
    };
}
