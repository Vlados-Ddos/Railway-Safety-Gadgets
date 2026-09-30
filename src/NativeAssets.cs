using System;
using System.Collections.Generic;
using System.Linq;
using DV.Customization.Gadgets;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    internal static class NativeAssets
    {
        private static readonly Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();
        internal static AudioClip Caution, Stop, Change, Alarm, Button;
        internal static string ButtonPrefabId, SwitchPrefabId;
        internal static string RotarySwitchPrefabId, AlternatingPrefabId, AnalogPrefabId;
        private static bool loaded;

        internal static GameObject Prefab(string resource)
        {
            GameObject result;
            if (!Prefabs.TryGetValue(resource, out result) || result == null)
            {
                result = Resources.Load<GameObject>(resource);
                if (result == null) throw new InvalidOperationException("Native item resource missing: " + resource);
                Prefabs[resource] = result;
            }
            return result;
        }

        internal static void Load()
        {
            if (loaded) return;
            // Exact ResourceManager keys, verified in this game's data. Their
            // referenced LampControl/Button clips are loaded with the prefabs.
            foreach (string key in new[] { "automatictrainstop", "brakecylinderledbar", "switchsetter", "wirelessmucontroller", "digitalspeedometer" }) Prefab(key);
            ButtonPrefabId = Prefab("switchbutton").GetComponent<InventoryItemSpec>().ItemPrefabName;
            SwitchPrefabId = Prefab("switchlever").GetComponent<InventoryItemSpec>().ItemPrefabName;
            RotarySwitchPrefabId = Prefab("switchrotary").GetComponent<InventoryItemSpec>().ItemPrefabName;
            AlternatingPrefabId = Prefab("switchalternating").GetComponent<InventoryItemSpec>().ItemPrefabName;
            AnalogPrefabId = Prefab("switchanalog").GetComponent<InventoryItemSpec>().ItemPrefabName;
            var clips = Resources.FindObjectsOfTypeAll<AudioClip>();
            Caution = Find(clips, "GadgetWarning_BrakeCylinderLEDBar_Blink");
            Stop = Find(clips, "GadgetWarning_WirelessMUController_Conflict");
            Change = Find(clips, "GadgetWarning_SwitchSetter_Detect");
            Alarm = Find(clips, "GadgetWarning_AutomaticTrainStop_TimeoutDelay");
            Button = Find(clips, "ButtonClick_02_Push");
            loaded = true;
        }

        private static AudioClip Find(AudioClip[] clips, string name)
        {
            var result = clips.FirstOrDefault(c => c != null && c.name == name);
            if (result == null) Main.ErrorOnce("audio-" + name, new InvalidOperationException("Native clip not loaded: " + name));
            return result;
        }
    }

    internal sealed class GadgetAudio
    {
        private readonly AudioSource oneShot;
        private readonly Transform owner;
        private AudioSource alarm;
        private float lastVolume = -1;
        private bool stopped = true;
        private DeviceKind deviceKind;
        internal void SetKind(DeviceKind kind) { deviceKind = kind; }
        internal GadgetAudio(Transform owner, DeviceKind kind)
        {
            this.owner = owner;
            deviceKind = kind;
            oneShot = Create(owner, "RSG_warnings");
        }
        private static AudioSource Create(Transform parent, string name)
        {
            var obj = new GameObject(name); obj.transform.SetParent(parent, false);
            var source = obj.AddComponent<AudioSource>();
            source.playOnAwake = false; source.spatialBlend = 1; source.dopplerLevel = 0;
            source.minDistance = .8f; source.maxDistance = 12;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            if (AudioManager.Instance != null) source.outputAudioMixerGroup = AudioManager.Instance.cabGroup;
            return source;
        }
        internal void Play(WarningKind kind)
        {
            float volume = Main.Settings.Volume(deviceKind);
            if (volume <= 0 || Time.timeScale <= 0 || UnloadWatcher.isUnloading) return;
            AudioClip clip = kind == WarningKind.Stop ? NativeAssets.Stop : kind == WarningKind.Caution ? NativeAssets.Caution : kind == WarningKind.Change ? NativeAssets.Change : null;
            if (clip == null) return;
            oneShot.Stop(); oneShot.clip = clip;
            SetVolume(volume);
            oneShot.Play(); stopped = false;
        }
        private void SetVolume(float volume)
        {
            if (lastVolume == volume) return;
            oneShot.volume = volume;
            if (alarm != null) alarm.volume = volume;
            lastVolume = volume;
        }
        internal void Update(bool powered, bool alarmPending)
        {
            float volume = Main.Settings.Volume(deviceKind);
            SetVolume(volume);
            if (!powered || Time.timeScale <= 0 || UnloadWatcher.isUnloading || volume <= 0)
            {
                StopAll(); return;
            }
            if (alarmPending && NativeAssets.Alarm != null)
            {
                if (alarm == null)
                {
                    alarm = Create(owner, "RSG_alarm");
                    alarm.clip = NativeAssets.Alarm; alarm.loop = true; alarm.volume = volume;
                }
                if (!alarm.isPlaying) alarm.Play();
                stopped = false;
            }
            else if (alarm != null && alarm.isPlaying) alarm.Stop();
        }
        internal void StopAll()
        {
            if (stopped) return;
            oneShot.Stop(); if (alarm != null) alarm.Stop(); stopped = true;
        }
    }
}
