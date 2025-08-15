using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
[HarmonyPatch]
public class LocalLoadPatch
{
	[HarmonyPatch]
	public class LocalLoadPatch_WorldStaticData_XmlLoadInfo
	{
		public static MethodBase TargetMethod()
		{
			return AccessTools.Constructor(AccessTools.Inner(typeof(WorldStaticData), "XmlLoadInfo"), new Type[10]
			{
				typeof(string),
				typeof(bool),
				typeof(bool),
				typeof(Func<XmlFile, IEnumerator>),
				typeof(Action),
				typeof(Func<IEnumerator>),
				typeof(bool),
				typeof(Action<XmlFile>),
				typeof(bool),
				typeof(string)
			}, false);
		}

		public static void Postfix(string _xmlName, bool _loadAtStartup, ref bool _sendToClients, Func<XmlFile, IEnumerator> _loadMethod, Action _cleanupMethod, ref bool ___LoadClientFile, Func<IEnumerator> _executeAfterLoad = null, bool _allowReloadDuringGame = false, Action<XmlFile> _reloadDuringGameMethod = null, bool _ignoreMissingFile = false, string _loadStepLocalizationKey = null)
		{
			if (_xmlName.Contains("XUi") || _xmlName.Contains("qualityinfo"))
			{
				// Debug.Log("<color=#00FF00>local load _xmlName: </color>" + _xmlName);
				___LoadClientFile = true;
			}
		}
	}

	[HarmonyPatch(typeof(WorldStaticData))]
	public class LocalLoadPatch_WorldStaticData
	{
		[HarmonyPostfix]
		[HarmonyPatch("ReceivedConfigFile")]
		public static void ReceivedConfigFile(string _name, byte[] _data)
		{
			if (!(_name.Contains("XUi") || _name.Contains("qualityinfo")))
			{
				return;
			}
			Debug.Log("<color=#00FF00>CATUI local load xml name: </color>" + _name);
			WorldStaticData.XmlLoadInfo[] xmlsToLoad = WorldStaticData.xmlsToLoad;
			foreach (WorldStaticData.XmlLoadInfo xmlLoadInfo in xmlsToLoad)
			{
				if (xmlLoadInfo.XmlName.Equals(_name))
				{
					xmlLoadInfo.WasReceivedFromServer = WorldStaticData.EClientFileState.LoadLocal;
					break;
				}
			}
		}
	}
}
