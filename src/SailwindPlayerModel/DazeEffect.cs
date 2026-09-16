using HarmonyLib;
using UnityEngine;
using UnityEngine.PostProcessing;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Seeing stars after a hard knock: the screen goes dark at the hit and lightens back up while the player lies there
    /// and gets up, with the vignette pulled in and stars drifting over the picture. Nothing written, only the picture.
    ///
    /// All three are the game's own: the screen fade it falls asleep with (OVRScreenFade on the eye camera), the vignette
    /// its tiredness warning drives (PlayerNeedsWarningEffects sets it every Update, so it is pushed further here, later
    /// in the frame), and the stars from its sleep screen (SleepUI's particles, copied, since SleepUI switches its own
    /// off every frame the player is awake).
    /// </summary>
    internal sealed class DazeEffect
    {
        private const float MostDark = 0.82f;

        private float _level;
        private bool _fadeSet;
        private ParticleSystem _stars;
        private float _starRate;
        private bool _starsTried;

        private static readonly AccessTools.FieldRef<PlayerNeedsWarningEffects, PostProcessingProfile> ProfileRef =
            AccessTools.FieldRefAccess<PlayerNeedsWarningEffects, PostProcessingProfile>("postProcessing");
        private static readonly AccessTools.FieldRef<SleepUI, ParticleSystem> SleepStarsRef =
            AccessTools.FieldRefAccess<SleepUI, ParticleSystem>("particles");

        public float Level { get { return _level; } }

        /// <summary>A knock of <paramref name="strength01"/>: at least that dazed, at once.</summary>
        public void Hit(float strength01)
        {
            _level = Mathf.Max(_level, Mathf.Clamp01(strength01));
        }

        /// <summary>From LateUpdate every frame: clear at <paramref name="perSecond"/> and draw what is left.</summary>
        public void Tick(float perSecond, float dt)
        {
            if (_level <= 0f && !_fadeSet && (_stars == null || !_stars.emission.enabled)) return;
            _level = Mathf.Max(0f, _level - perSecond * dt);
            Apply();
        }

        public void Clear()
        {
            _level = 0f;
            Apply();
        }

        private void Apply()
        {
            // Straight from the level: an ease here made anything short of a big fall too faint to notice.
            float k = _level;

            var cam = Camera.main;
            var fade = cam != null ? cam.GetComponent<OVRScreenFade>() : null;
            if (fade != null)
            {
                if (k > 0.001f)
                {
                    fade.SetFadeLevel(MostDark * k);
                    _fadeSet = true;
                }
                else if (_fadeSet)
                {
                    fade.SetFadeLevel(0f);
                    _fadeSet = false;
                }
            }
            else _fadeSet = false;

            var warning = PlayerNeedsWarningEffects.instance;
            var profile = warning != null ? ProfileRef(warning) : null;
            if (profile != null && k > 0.001f)
            {
                var s = profile.vignette.settings;
                s.intensity = Mathf.Lerp(s.intensity, 1f, k);
                profile.vignette.settings = s;
            }

            if (_stars == null && !_starsTried && k > 0.001f) BuildStars();
            if (_stars != null)
            {
                var emission = _stars.emission;
                emission.enabled = k > 0.05f;
                emission.rateOverTimeMultiplier = _starRate * k;
            }
        }

        private void BuildStars()
        {
            _starsTried = true;
            try
            {
                var ui = Object.FindObjectOfType<SleepUI>();
                var source = ui != null ? SleepStarsRef(ui) : null;
                if (source == null)
                {
                    Plugin.Log.LogInfo("[Downed] no sleep stars to borrow; the knock goes without them");
                    return;
                }
                // The sleep screen's own holder is switched off while awake; hang the copy off the nearest one that is on.
                Transform parent = source.transform.parent;
                while (parent != null && !parent.gameObject.activeInHierarchy) parent = parent.parent;
                var go = Object.Instantiate(source.gameObject, source.transform.position, source.transform.rotation, parent);
                go.name = "PlayerModelDazeStars";
                foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true)) Object.Destroy(behaviour);
                go.SetActive(true);
                _stars = go.GetComponent<ParticleSystem>();
                if (_stars == null) { Object.Destroy(go); return; }
                // Riding along with the view, not left behind where the knock happened.
                var main = _stars.main;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                _starRate = source.emission.rateOverTimeMultiplier;
                if (_starRate <= 0f) _starRate = 20f;
                var emission = _stars.emission;
                emission.enabled = false;
                _stars.Play(true);
                Plugin.Log.LogInfo($"[Downed] borrowed the sleep stars ({_starRate:F0} a second at most), under '{(parent != null ? parent.name : "nothing")}'");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Downed] could not borrow the sleep stars: " + e.Message);
                _stars = null;
            }
        }
    }
}
