using System;
using UnityEngine;

// Ported from Marbly (AudioManager.cs): static, PlayerPrefs-backed music/SFX volumes, lazily
// created persistent AudioSources. Keys renamed to "Kuzya.*"; clips are looked up by name from
// the GameArt catalog (Assets/Sounds/*.mp3).
public static class AudioManager
{
    const string MusicVolumePrefKey = "Kuzya.MusicVolume";
    const string SfxVolumePrefKey = "Kuzya.SfxVolume";

    public static event Action OnAudioSettingsChanged;

    static float? musicVolume;
    public static float MusicVolume
    {
        get { if (musicVolume == null) musicVolume = PlayerPrefs.GetFloat(MusicVolumePrefKey, 0.6f); return musicVolume.Value; }
    }

    static float? sfxVolume;
    public static float SfxVolume
    {
        get { if (sfxVolume == null) sfxVolume = PlayerPrefs.GetFloat(SfxVolumePrefKey, 1f); return sfxVolume.Value; }
    }

    public static void SetMusicVolume(float v)
    {
        v = Mathf.Clamp01(v);
        if (Mathf.Approximately(MusicVolume, v)) return;
        musicVolume = v;
        PlayerPrefs.SetFloat(MusicVolumePrefKey, v);
        if (musicSource != null) musicSource.volume = v * musicDuck;
        OnAudioSettingsChanged?.Invoke();
    }

    public static void SetSfxVolume(float v)
    {
        v = Mathf.Clamp01(v);
        if (Mathf.Approximately(SfxVolume, v)) return;
        sfxVolume = v;
        PlayerPrefs.SetFloat(SfxVolumePrefKey, v);
        OnAudioSettingsChanged?.Invoke();
    }

    static AudioSource sfxSource;
    static AudioSource GetSfxSource()
    {
        if (sfxSource != null) return sfxSource;
        var go = new GameObject("AudioManager.SfxSource");
        UnityEngine.Object.DontDestroyOnLoad(go);
        sfxSource = go.AddComponent<AudioSource>();
        sfxSource.playOnAwake = false;
        return sfxSource;
    }

    static float lastPlayTime;
    static string lastPlayKey;

    public static void Play(string key, float volume = 1f)
    {
        // de-duplicate the same clip fired many times in one frame (a big blast pops 20 cats)
        if (key == lastPlayKey && Time.unscaledTime - lastPlayTime < 0.035f) return;
        lastPlayKey = key; lastPlayTime = Time.unscaledTime;
        PlaySfx(GameArt.I.Clip(key), volume);
    }

    public static void PlaySfx(AudioClip clip, float volume = 1f)
    {
        if (clip == null) return;
        float v = volume * SfxVolume;
        if (v <= 0f) return;
        GetSfxSource().PlayOneShot(clip, v);
    }

    public static void PlayClick() { Play("click", 0.8f); }

    static AudioSource musicSource;
    public static void PlayMusic(string key)
    {
        var clip = GameArt.I.Clip(key);
        if (clip == null) return;
        if (musicSource == null)
        {
            var go = new GameObject("AudioManager.MusicSource");
            UnityEngine.Object.DontDestroyOnLoad(go);
            musicSource = go.AddComponent<AudioSource>();
            musicSource.playOnAwake = false;
            musicSource.loop = true;
        }
        musicSource.volume = MusicVolume * musicDuck;
        if (musicSource.clip == clip && musicSource.isPlaying) return;
        musicSource.clip = clip;
        musicSource.Play();
    }

    // temporary music dip (e.g. under the win / lose jingles): 1 = normal, 0 = silent
    static float musicDuck = 1f;
    public static void SetMusicDuck(float d)
    {
        musicDuck = Mathf.Clamp01(d);
        if (musicSource != null) musicSource.volume = MusicVolume * musicDuck;
    }

    public static void StopMusic() { if (musicSource != null) musicSource.Stop(); }
}
