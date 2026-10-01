using SCFA.ContentCenter.Core;

internal static class ClientUpdateVersionRegression
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var (current, latest, sign, name) in new[]
        {
            ("4.0.0-dev61", "4.0.0-dev62", -1, "开发版客户端按dev序号升级"),
            ("4.0.0-dev62", "4.0.0-dev61", 1, "开发版客户端拒绝降级"),
            ("4.0.0-dev9", "4.0.0-dev10", -1, "开发版序号按数值而非字典排序"),
            ("4.0.0-beta.2", "4.0.0-rc1", -1, "客户端beta与rc阶段可排序"),
            ("4.0.0-dev62", "4.0.0", -1, "正式客户端高于同基线开发版"),
            ("4.0.0", "4.0.0-beta9", 1, "同基线测试版不能降低正式客户端"),
            ("4.0.0", "4.1.0-dev1", -1, "客户端先比较数字主版本"),
            ("v4.0.0-dev62+build.a", "4.0.0-dev62+build.b", 0, "客户端构建标识不改变发布版本顺序"),
            ("4.0.0", "4.0.0.0", 0, "客户端三段与尾零四段版本等价")
        })
        {
            var result = ClientVersion.Compare(current, latest);
            check(result.Ordered && Math.Sign(result.Compare) == sign, name);
        }
        check(!ClientVersion.Compare("4.0.0-preview1", "4.0.0-dev62").Ordered &&
              !ClientVersion.Compare("4.0.0-dev999999999999", "4.0.0-dev62").Ordered &&
              !ClientVersion.Compare("", "4.0.0").Ordered,
              "客户端未知标签、溢出和缺失版本拒绝猜测顺序");
        check(!ContentIdentity.CompareVersions("4.0.0-dev61", "4.0.0-dev62").Ordered,
              "客户端版本改动保留地图与MOD原有标签版本保护");
    }
}
