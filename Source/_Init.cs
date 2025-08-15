// 引入所需的命名空间
using System.Reflection;
using HarmonyLib;

// 定义一个名为ModStartup的公共类，继承自IModApi接口
public class ModStartup : IModApi
{
    // 初始化方法
    public void InitMod(Mod modInstance)
    {
        // 加载Assembly-CSharp.dll
        Assembly executeAssembly = Assembly.GetExecutingAssembly();

        // 创建Harmony实例
        Harmony harmony = new Harmony(executeAssembly.GetName().Name);

        // 打补丁
        harmony.PatchAll(executeAssembly);
    }
}