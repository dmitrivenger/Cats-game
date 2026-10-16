using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Advertisements;
using UnityEngine.UI;

// Ads: one small wrapper around Unity Ads, so the game never talks to the ad SDK directly.
// Swapping the network later (e.g. to LevelPlay for Google Play) means rewriting only this file.
//
//  * Rewarded ads: the player chooses to watch and gets something (moves, coins, a cat...).
//  * Interstitials: a full-screen ad shown by the game itself - only after a LOST level, from level 6,
//    at most once every 3 minutes, never right after the player watched a rewarded ad.
//  * Consent (UK/EU): asked once; the answer goes to Unity Ads as GDPR/privacy metadata.
//
// Until a real Game ID is filled in below, ads are off on devices (the ad buttons simply don't
// appear). In the Unity Editor a stand-in "test ad" is shown instead, so every flow can be tried.
public class Ads : MonoBehaviour, IUnityAdsInitializationListener, IUnityAdsLoadListener, IUnityAdsShowListener
{
    // ---------------------------------------------------------------- settings
    // Android Game ID from the Unity Cloud dashboard (Monetization -> Ad Units). Empty = ads off on devices.
    public const string AndroidGameId = "800396013";
    // Keep true until release: test mode shows Unity's sample ads and never pays out.
    public const bool TestMode = true;
    // The single switch for full-screen ads after a lost level.
    public const bool InterstitialsOn = true;
    const string RewardedUnit = "BP_Rewarded_Android", InterstitialUnit = "BP_Interstitial_Android";   // placement IDs from the Unity dashboard
    public const int InterstitialFromLevel = 6;
    const float InterstitialCooldown = 180f;          // seconds between two full-screen ads
    const float AfterRewardedQuiet = 90f;              // no full-screen ad right after a rewarded one
    public const int CoinReward = 25;                  // "not enough coins" -> watch an ad
    public const int CoinAdsPerDay = 5;

    const string ConsentKey = "Kuzya.AdsConsent";      // -1 not asked, 0 plain ads, 1 personalised
    const string CoinDayKey = "Kuzya.AdsCoinDay", CoinCountKey = "Kuzya.AdsCoinCount";

    static Ads I;
    bool initialised, rewardedReady, interReady, showing;
    Action<bool> rewardedDone; Action interDone;
    float lastInter = -9999f, lastRewarded = -9999f;

    // ---------------------------------------------------------------- public API
    // ads exist at all on this build: a real Game ID, or the Editor's stand-in
    public static bool Enabled => !string.IsNullOrEmpty(AndroidGameId) || Application.isEditor;
    static bool Fake => string.IsNullOrEmpty(AndroidGameId) && Application.isEditor;

    public static bool ConsentAsked => PlayerPrefs.GetInt(ConsentKey, -1) >= 0;
    public static bool Personalised => PlayerPrefs.GetInt(ConsentKey, -1) == 1;

    public static void SetConsent(bool personalised)
    {
        PlayerPrefs.SetInt(ConsentKey, personalised ? 1 : 0); PlayerPrefs.Save();
        if (I != null && !Fake) I.ApplyConsent();
    }

    public static void Init()
    {
        if (I != null || !Enabled) return;
        var go = new GameObject("Ads");
        DontDestroyOnLoad(go);
        I = go.AddComponent<Ads>();
        I.Begin();
    }

    // a rewarded ad can be offered right now (a button for it may be shown)
    public static bool CanReward => I != null && !I.showing && (Fake || I.rewardedReady);

    // shows a rewarded ad; done(true) only if it was watched to the end
    public static void ShowRewarded(Action<bool> done)
    {
        if (!CanReward) { done?.Invoke(false); return; }
        I.rewardedDone = done; I.showing = true; I.showStart = Time.realtimeSinceStartup;
        if (Fake) { I.StartCoroutine(I.FakeAd(true)); return; }
        Advertisement.Show(RewardedUnit, I);
    }

    // after a lost level: maybe a full-screen ad, then `then` (always called, ad or not)
    public static void AfterLoss(int level, Action then)
    {
        if (I == null || I.showing || !InterstitialsOn || level < InterstitialFromLevel
            || Time.realtimeSinceStartup - I.lastInter < InterstitialCooldown
            || Time.realtimeSinceStartup - I.lastRewarded < AfterRewardedQuiet
            || !(Fake || I.interReady))
        { then?.Invoke(); return; }
        I.interDone = then; I.showing = true; I.lastInter = I.showStart = Time.realtimeSinceStartup;
        if (Fake) { I.StartCoroutine(I.FakeAd(false)); return; }
        Advertisement.Show(InterstitialUnit, I);
    }

    // "not enough coins" ads are capped per day so they can't replace playing
    public static bool CoinAdLeft
    {
        get
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (PlayerPrefs.GetString(CoinDayKey, "") != today) return true;
            return PlayerPrefs.GetInt(CoinCountKey, 0) < CoinAdsPerDay;
        }
    }

    public static void CountCoinAd()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (PlayerPrefs.GetString(CoinDayKey, "") != today) { PlayerPrefs.SetString(CoinDayKey, today); PlayerPrefs.SetInt(CoinCountKey, 0); }
        PlayerPrefs.SetInt(CoinCountKey, PlayerPrefs.GetInt(CoinCountKey, 0) + 1);
        PlayerPrefs.Save();
    }

    // ---------------------------------------------------------------- setup
    void Begin()
    {
        if (Fake) { initialised = true; return; }
        if (!Advertisement.isSupported) return;
        ApplyConsent();
        Advertisement.Initialize(AndroidGameId, TestMode, this);
    }

    void ApplyConsent()
    {
        var gdpr = new MetaData("gdpr"); gdpr.Set("consent", Personalised ? "true" : "false"); Advertisement.SetMetaData(gdpr);
        var privacy = new MetaData("privacy"); privacy.Set("consent", Personalised ? "true" : "false"); Advertisement.SetMetaData(privacy);
    }

    void LoadAll()
    {
        if (!rewardedReady) Advertisement.Load(RewardedUnit, this);
        if (InterstitialsOn && !interReady) Advertisement.Load(InterstitialUnit, this);
    }

    IEnumerator RetryLater(string unit)
    {
        yield return new WaitForSecondsRealtime(30f);
        if (initialised) Advertisement.Load(unit, this);
    }

    // ---------------------------------------------------------------- Unity Ads callbacks
    public void OnInitializationComplete() { initialised = true; LoadAll(); }
    public void OnInitializationFailed(UnityAdsInitializationError error, string message)
    {
        Debug.LogWarning("Ads init failed: " + error + " " + message);
        StartCoroutine(RetryInit());               // e.g. no internet at launch: try again a little later
    }

    IEnumerator RetryInit()
    {
        yield return new WaitForSecondsRealtime(45f);
        if (!initialised) Advertisement.Initialize(AndroidGameId, TestMode, this);
    }

    // safety net: if the ad network never reports the end of an ad, don't keep the ad buttons hidden forever
    float showStart;
    void Update()
    {
        if (showing && !Fake && Time.realtimeSinceStartup - showStart > 120f) Finish(rewardedDone != null ? RewardedUnit : InterstitialUnit, false);
    }

    public void OnUnityAdsAdLoaded(string unit)
    {
        if (unit == RewardedUnit) rewardedReady = true;
        if (unit == InterstitialUnit) interReady = true;
    }

    public void OnUnityAdsFailedToLoad(string unit, UnityAdsLoadError error, string message)
    {
        Debug.LogWarning("Ad load failed: " + unit + " " + error + " " + message);
        StartCoroutine(RetryLater(unit));
    }

    public void OnUnityAdsShowFailure(string unit, UnityAdsShowError error, string message) { Finish(unit, false); }
    public void OnUnityAdsShowStart(string unit) { }
    public void OnUnityAdsShowClick(string unit) { }
    public void OnUnityAdsShowComplete(string unit, UnityAdsShowCompletionState state) { Finish(unit, state == UnityAdsShowCompletionState.COMPLETED); }

    void Finish(string unit, bool completed)
    {
        showing = false;
        if (unit == RewardedUnit)
        {
            rewardedReady = false;
            if (completed) lastRewarded = Time.realtimeSinceStartup;
            var cb = rewardedDone; rewardedDone = null;
            if (!Fake) Advertisement.Load(RewardedUnit, this);
            cb?.Invoke(completed);
        }
        else
        {
            interReady = false;
            var cb = interDone; interDone = null;
            if (!Fake) Advertisement.Load(InterstitialUnit, this);
            cb?.Invoke();
        }
    }

    // ---------------------------------------------------------------- Editor stand-in
    // A dark full-screen card "test ad" with a countdown; the rewarded one pays out at the end.
    IEnumerator FakeAd(bool rewarded)
    {
        var canvas = UIKit.CreateCanvas("FakeAd", 2000);
        var root = UIKit.Rect("ad", canvas.transform); UIKit.Stretch(root);
        var dim = root.gameObject.AddComponent<Image>(); dim.color = new Color(0.05f, 0.05f, 0.1f, 0.96f);
        UIKit.Label(root, Loc.T("Ads.Fake"), 60, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, 120), new Vector2(980, 200));
        var count = UIKit.Label(root, "", 120, new Color(1f, 0.85f, 0.35f), new Vector2(0.5f, 0.5f), new Vector2(0, -80), new Vector2(400, 160), true);
        float left = rewarded ? 3f : 2f;
        while (left > 0f) { count.text = Mathf.CeilToInt(left).ToString(); left -= Time.unscaledDeltaTime; yield return null; }
        Destroy(canvas.gameObject);
        Finish(rewarded ? RewardedUnit : InterstitialUnit, true);
    }
}
