using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using LC_API.ServerAPI;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SocialPlatforms;

namespace LAN;

[BepInPlugin(ID, NAME, VERSION)]
public class Plugin : BaseUnityPlugin
{
    public const string ID = "com.exp111.LAN";
    public const string NAME = "LAN";
    public const string VERSION = "1.0";

    public static ManualLogSource Log;
    private static Harmony Harmony;

    public static ConfigEntry<string> ServerIP;
    public static ConfigEntry<string> Name;

    private void Awake()
    {
        try
        {
            Log = Logger;
            Log.LogMessage("Awake");

            SetupConfig();
            LC_API.ServerAPI.Networking.GetString = OnStringReceive;

            Harmony = new Harmony(ID);
            Harmony.PatchAll();
        }
        catch (Exception e)
        {
            Log.LogMessage($"Exception during LAN.Awake: {e}");
        }
    }

    private void SetupConfig()
    {
        ServerIP = Config.Bind("General", "URL", "127.0.0.1", "The server ip which to connect");
        Name = Config.Bind("General", "Name", "");
    }

    // Gets messages
    private void OnStringReceive(string message, string signature)
    {
        try
        {
            if (signature.Equals("LAN"))
            {
                Log.LogMessage($"Got a msg ({signature}): {message}");
                var msg = JsonUtility.FromJson<NameMessage>(message);
                if (msg != null)
                {
                    var local = GameNetworkManager.Instance.localPlayerController;
                    // is the message targeted to someone else?
                    if (msg.Receiver != -1 && msg.Receiver != (int)local.actualClientId)
                        return;
                    var id = msg.ID;
                    var name = msg.Name;
                    if (!SetPlayerName(local, id, name))
                        return;
                    // its a broadcast, respond with our name to them
                    if (!msg.Response)
                    {
                        Log.LogMessage($"Sending a response to: {id}");
                        LC_API.ServerAPI.Networking.Broadcast(JsonUtility.ToJson(new NameMessage { Name = Name.Value, ID = local.actualClientId, Receiver = (int)id, Response = true }), "LAN");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.LogMessage($"Exception during OnStringReceive hook: {e}");
        }
    }

    //INFO: do not readd this as the plugin is immediately destroyed for some reason
    /*public void OnDestroy()
    {
        Log.LogMessage($"Destroying");
        // Delete your stuff
        Harmony?.UnpatchSelf();
    }*/

    // Sets the name of a player with the given client id
    public static bool SetPlayerName(PlayerControllerB local, ulong actualClientId, string name)
    {
        PlayerControllerB player = null;
        for (var i = 0; i < local.playersManager.allPlayerScripts.Length; i++)
        {
            var p = local.playersManager.allPlayerScripts[i];
            if (p.playerClientId == actualClientId)
            {
                player = p;
                break;
            }
        }
        if (player == null)
        {
            Log.LogMessage($"Did not find player with clientid {actualClientId}");
            foreach (var p in local.playersManager.allPlayerScripts)
            {
                Log.LogMessage($"Player: {p}, id: {p.actualClientId}, network {p.NetworkObjectId}, clientId {p.playerClientId}");
            }
            return false;
        }
        if (player.isPlayerControlled || player.isPlayerDead)
        {
            player.playerUsername = name;
            player.usernameBillboardText.text = name;
            local.quickMenuManager.AddUserToPlayerList(0, name, (int)player.playerClientId);
            StartOfRound.Instance.mapScreen.radarTargets[(int)player.playerClientId].name = name;
        }
        return true;
    }

}

public class NameMessage
{
    public string Name;
    public ulong ID;
    public int Receiver = -1;
    public bool Response = false;
}

// Sets the ip to connect to
//[HarmonyDebug]
[HarmonyPatch(typeof(MenuManager), nameof(MenuManager.StartAClient))]
public class MenuManager_StartAClient_Patch
{
    [HarmonyPrefix]
    public static void Prefix(MenuManager __instance)
    {
        try
        {
            NetworkManager.Singleton.GetComponent<UnityTransport>().ConnectionData.Address = Plugin.ServerIP.Value;
            Plugin.Log.LogMessage($"Set IP to {Plugin.ServerIP.Value}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogMessage($"Exception during MenuManager.StartAClient hook: {e}");
        }
    }
}

// Changes the default name to <Nr>Player instead of Player #<Nr>, so they can be switched in the terminal
[HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.Awake))]
class PlayerControllerB_Awake_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            var fieldInfo = AccessTools.Field(typeof(PlayerControllerB), nameof(PlayerControllerB.playerUsername));
            var cur = new CodeMatcher(instructions);
            // find hardcoded player #0
            cur.MatchForward(false, new CodeMatch(OpCodes.Ldstr, "Player #{0}"));
            // replace
            cur.SetInstruction(new CodeInstruction(OpCodes.Ldstr, "{0}Player"));

            var e = cur.InstructionEnumeration();
            /*foreach (var code in e)
            {
                Plugin.Log.LogMessage(code);
            }*/
            return e;
        }
        catch (Exception e)
        {
            Plugin.Log.LogMessage($"Exception during PlayerControllerB.ConnectClientToPlayerObject transpiler: {e}");
        }
        return instructions;
    }
}

// Set your name and set it to other players
[HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.ConnectClientToPlayerObject))]
class PlayerControllerB_Name_Patch
{
    [HarmonyPostfix]
    public static void Postfix(PlayerControllerB __instance)
    {
        try
        {
            Plugin.Log.LogMessage($"changing name to: {Plugin.Name.Value}");
            if (!Plugin.SetPlayerName(__instance, __instance.playerClientId, Plugin.Name.Value))
                return;
            LC_API.ServerAPI.Networking.Broadcast(JsonUtility.ToJson(new NameMessage { Name = Plugin.Name.Value, ID = __instance.actualClientId }), "LAN");
        }
        catch (Exception e)
        {
            Plugin.Log.LogMessage($"Exception during PlayerControllerB.ConnectClientToPlayerObject transpiler: {e}");
        }
    }
}

// Use the player username instead of Player#0 for the menu local add, may be useless?
//[HarmonyDebug]
[HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.ConnectClientToPlayerObject))]
class PlayerControllerB_ConnectClientToPlayerObject_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            var fieldInfo = AccessTools.Field(typeof(PlayerControllerB), nameof(PlayerControllerB.playerUsername));
            var cur = new CodeMatcher(instructions);
            // find hardcoded player #0
            cur.MatchForward(false, new CodeMatch(OpCodes.Ldstr, "Player #0"));
            // replace with this.playerUsername
            cur.InsertAndAdvance(new CodeInstruction(OpCodes.Ldarg_0));
            cur.SetInstruction(new CodeInstruction(OpCodes.Ldfld, fieldInfo));

            var e = cur.InstructionEnumeration();
            /*foreach (var code in e)
            {
                Plugin.Log.LogMessage(code);
            }*/
            return e;
        }
        catch (Exception e)
        {
            Plugin.Log.LogMessage($"Exception during PlayerControllerB.ConnectClientToPlayerObject transpiler: {e}");
        }
        return instructions;
    }
}