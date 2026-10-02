using System.IO;
using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

internal static class ModPreviewRegression
{
    internal static async Task RunAsync(string testRoot, LogService log, Action<bool, string> check)
    {
        var dxt1 = Dds("DXT1");
        BinaryPrimitives.WriteUInt16LittleEndian(dxt1.AsSpan(128), 0xF800);
        BinaryPrimitives.WriteUInt16LittleEndian(dxt1.AsSpan(130), 0x07E0);
        check(Pixel(DdsPreviewDecoder.TryDecode(dxt1)).SequenceEqual(new byte[] { 0, 0, 255, 255 }), "DDS DXT1按RGB565读取红色");
        BinaryPrimitives.WriteUInt16LittleEndian(dxt1.AsSpan(128), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(dxt1.AsSpan(130), 65535);
        BinaryPrimitives.WriteUInt32LittleEndian(dxt1.AsSpan(132), uint.MaxValue);
        check(Pixel(DdsPreviewDecoder.TryDecode(dxt1))[3] == 0, "DDS DXT1保留单色透明");
        var dxt3 = Dds("DXT3");
        dxt3.AsSpan(128, 8).Fill(0x88);
        BinaryPrimitives.WriteUInt16LittleEndian(dxt3.AsSpan(136), 0xF800);
        check(Pixel(DdsPreviewDecoder.TryDecode(dxt3)).SequenceEqual(new byte[] { 0, 0, 255, 136 }), "DDS DXT3保留显式透明度");
        var dxt5 = Dds("DXT5");
        dxt5[128] = 200; dxt5[129] = 100; dxt5[130] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dxt5.AsSpan(136), 0xF800);
        check(Pixel(DdsPreviewDecoder.TryDecode(dxt5))[3] == 185, "DDS DXT5七步透明度插值");
        dxt5[128] = 10; dxt5[129] = 20; dxt5[130] = 6 | (7 << 3);
        var alphaPixels = new byte[64]; DdsPreviewDecoder.TryDecode(dxt5)!.CopyPixels(alphaPixels, 16, 0);
        check(alphaPixels[3] == 0 && alphaPixels[7] == 255, "DDS DXT5五步分支保留全透明与不透明");
        Set(dxt5, 12, 1); Set(dxt5, 16, 1);
        check(DdsPreviewDecoder.TryDecode(dxt5) is { PixelWidth: 1, PixelHeight: 1, IsFrozen: true }, "DDS非4倍数尺寸裁去块边缘并冻结图像");
        check(DdsPreviewDecoder.TryDecode(dxt5.AsSpan(0, 140)) is null, "截断DDS块不崩溃");
        Set(dxt5, 16, uint.MaxValue);
        check(DdsPreviewDecoder.TryDecode(dxt5) is null, "DDS异常尺寸在分配前拒绝");
        var unsupported = Dds("DX10");
        check(DdsPreviewDecoder.TryDecode(unsupported) is null, "未支持DDS格式安全返回空");
        Set(unsupported, 84, 0x35545844); Set(unsupported, 112, 0x200);
        check(DdsPreviewDecoder.TryDecode(unsupported) is null, "DDS立方贴图不当作MOD图标");
        var raw = new byte[132]; "DDS "u8.CopyTo(raw); Set(raw, 4, 124); Set(raw, 12, 1); Set(raw, 16, 1);
        Set(raw, 76, 32); Set(raw, 80, 0x41); Set(raw, 88, 32);
        Set(raw, 92, 0xFF0000); Set(raw, 96, 0xFF00); Set(raw, 100, 0xFF); Set(raw, 104, 0xFF000000);
        new byte[] { 12, 23, 34, 45 }.CopyTo(raw, 128);
        check(Pixel(DdsPreviewDecoder.TryDecode(raw)).SequenceEqual(new byte[] { 12, 23, 34, 45 }), "DDS未压缩BGRA保留颜色及透明度");
        Set(raw, 96, 0xFF);
        check(DdsPreviewDecoder.TryDecode(raw) is null, "DDS重叠颜色掩码被拒绝");

        var mods = Path.Combine(testRoot, "mod-icon-fixtures", "mods");
        var root = Path.Combine(mods, "Renamed"); Directory.CreateDirectory(root);
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 0 }, 8);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(root, "mod_icon.png"))) png.Save(output);
        void Info(string icon) => File.WriteAllText(Path.Combine(root, "mod_info.lua"),
            "name = 'Icon MOD'\nuid = 'icon-mod-fixture'\nversion = 1\n" + icon);
        Info("icon = '/mods/Original/mod_icon.png' -- icon retained after install rename\r\n");
        check(ModPreviewService.TryLoad(root) is { PixelWidth: 2, PixelHeight: 2, IsFrozen: true }, "MOD按icon声明读取小PNG并支持目录改名与行尾注释");
        check(MapPreviewService.TryLoad(root) is null, "旧地图预览路径未误认MOD专用图标");
        Info("--[=[\nicon = '/mods/Bad/fake.png'\n]=]\nicon = 'mod_icon.png'");
        check(ModPreviewService.TryLoad(root) is not null, "MOD跳过Lua长注释后读取相对图标路径");
        Info("-- icon = 'mod_icon.png'");
        check(ModPreviewService.TryLoad(root) is null, "MOD不读取注释中的icon声明");
        Info("icon = '../Renamed/mod_icon.png'");
        check(ModPreviewService.TryLoad(root) is null, "MOD图标不接受父目录跳转");
        Info("icon = 'https://example.com/icon.png'");
        check(ModPreviewService.TryLoad(root) is null, "MOD图标不执行网络地址或外部磁盘访问");
        Info("icon = '/mods/Original/missing.png'");
        check(ModPreviewService.TryLoad(root) is null, "MOD缺少图标不阻止列表加载");
        File.WriteAllBytes(Path.Combine(root, "mod_icon.dds"), Dds("DXT5"));
        Info("icon = '/mods/Original/mod_icon.dds'");
        check(ModPreviewService.TryLoad(root) is { PixelWidth: 4 }, "MOD的DDS声明进入解码模块");
        var nested = Path.Combine(mods, "Bundle", "Nested"); Directory.CreateDirectory(nested);
        File.Copy(Path.Combine(root, "mod_icon.png"), Path.Combine(nested, "mod_icon.png"));
        File.WriteAllText(Path.Combine(nested, "mod_info.lua"), "icon = '/mods/Bundle/Nested/mod_icon.png'");
        check(ModPreviewService.TryLoad(nested) is not null, "MOD套件嵌套虚拟路径对应实际目录");
        var flat = Path.Combine(mods, "Nested"); Directory.CreateDirectory(flat);
        File.Copy(Path.Combine(root, "mod_icon.png"), Path.Combine(flat, "mod_icon.png"));
        File.WriteAllText(Path.Combine(flat, "mod_info.lua"), "icon = '/mods/Bundle/Nested/mod_icon.png'");
        check(ModPreviewService.TryLoad(flat) is not null, "MOD拆包后保留的虚拟嵌套前缀可定位自身图标");
        Info("icon = '/mods/Original/mod_icon.png'");
        var config = new ConfigService(); config.Current.ModsDir = mods;
        var local = new LocalContentService(new GamePathService(config), log);
        var selected = await local.AnalyzeDirectoryAsync(root);
        var before = await ContentHash.DirectorySha256Async(root);
        var bundle = await new PublicationPreparationService(local).PrepareAsync(selected,
            new PublicationMetadata("Icon MOD", "2", "Test", "", "MOD", ""), """{"mods":[]}""",
            Path.Combine(testRoot, "mod-thumbnail-stage"), config.Current, new UserInfo { RoleKey = "admin" });
        check(bundle.ThumbnailPath is not null && File.Exists(bundle.ThumbnailPath) && bundle.ThumbnailKey is not null,
            "MOD发布材料生成PNG缩略图及对象键");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(bundle.ManifestPath));
        check(manifest!["mods"]![0]!["thumbnail"]!.GetValue<string>() == bundle.ThumbnailKey,
            "MOD清单关联生成的缩略图供未安装用户查看");
        using (var input = File.OpenRead(bundle.ThumbnailPath!))
            check(BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0].PixelWidth == 2,
                "MOD发布缩略图保留图标尺寸");
        check(before == await ContentHash.DirectorySha256Async(root), "图标读取及缩略图生成不修改MOD原文件");
        var cloud = new CloudContentEntry { Kind = "MOD", MapPreview = ModPreviewService.TryLoad(root) };
        check(cloud.HasPreview && cloud.DisplayPreviewSource is BitmapSource && cloud.PreviewStateText == "游戏 MOD 图标",
            "MOD解码结果可直接供云端列表预览绑定");
    }

    private static byte[] Dds(string format)
    {
        var data = new byte[144]; "DDS "u8.CopyTo(data);
        Set(data, 4, 124); Set(data, 12, 4); Set(data, 16, 4); Set(data, 76, 32); Set(data, 80, 4);
        System.Text.Encoding.ASCII.GetBytes(format).CopyTo(data, 84); return data;
    }
    private static void Set(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static byte[] Pixel(BitmapSource? bitmap)
    {
        if (bitmap is null) return [];
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels[..4];
    }
}
