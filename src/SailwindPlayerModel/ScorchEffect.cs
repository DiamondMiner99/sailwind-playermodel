using HarmonyLib;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Smoke, then flames, from the seat of a body's pants: what sitting on a lit stove too long does (see
    /// Seating), with steam when it is raining on them. Built from the game's own stove fire, smoke and crackle
    /// (StoveFuelTrigger) and a boiling kettle's steam and bubbling (CookableFoodKettle), copied once from whichever
    /// are loaded, so it looks like the stove it came from.
    ///
    /// The copies are not parented to the body: its renderers are switched off in first person, and the smoke and
    /// flames should still show there when looking down.
    /// </summary>
    internal sealed class ScorchEffect
    {
        private static readonly AccessTools.FieldRef<StoveFuelTrigger, ParticleSystem> FireRef =
            AccessTools.FieldRefAccess<StoveFuelTrigger, ParticleSystem>("fireParticles");
        private static readonly AccessTools.FieldRef<StoveFuelTrigger, ParticleSystem> SmokeRef =
            AccessTools.FieldRefAccess<StoveFuelTrigger, ParticleSystem>("smokeParticles");
        private static readonly AccessTools.FieldRef<CookableFoodKettle, ParticleSystem> BoilRef =
            AccessTools.FieldRefAccess<CookableFoodKettle, ParticleSystem>("boilParticles");

        private static StoveFuelTrigger _stove;
        private static CookableFoodKettle _kettle;
        private static float _nextLook;

        private GameObject _root;
        private ParticleSystem _fire, _smoke, _steam;
        private AudioSource _crackle, _hiss;
        private float _fireRate, _smokeRate, _steamRate;
        private float _smoke01, _fire01, _steam01;

        /// <summary>
        /// Place the effect at <paramref name="seat"/> and ease toward the given smoke, fire and steam amounts. Builds
        /// itself the first time any is asked for, and switches itself off once all have died away.
        /// </summary>
        public void Update(Vector3 seat, float smokeTarget, float fireTarget, float steamTarget, float dt)
        {
            if (_root == null)
            {
                if (smokeTarget <= 0.001f && fireTarget <= 0.001f && steamTarget <= 0.001f) return;
                if (!Build()) return;
            }
            _smoke01 = Mathf.MoveTowards(_smoke01, smokeTarget, dt * 1.5f);
            _fire01 = Mathf.MoveTowards(_fire01, fireTarget, dt * (fireTarget > _fire01 ? 4f : 1.2f));
            _steam01 = Mathf.MoveTowards(_steam01, steamTarget, dt * 2f);

            bool on = _smoke01 > 0.001f || _fire01 > 0.001f || _steam01 > 0.001f || Alive(_fire) || Alive(_smoke) || Alive(_steam);
            if (_root.activeSelf != on) _root.SetActive(on);
            if (!on) return;
            _root.transform.SetPositionAndRotation(seat, Quaternion.identity);

            SetRate(_fire, _fireRate * _fire01);
            SetRate(_smoke, _smokeRate * Mathf.Max(_smoke01, _fire01 * 0.8f));
            SetRate(_steam, _steamRate * _steam01);
            SetVolume(_crackle, Mathf.Max(_fire01, _smoke01 * 0.25f));
            SetVolume(_hiss, _steam01 * 0.7f);
        }

        /// <summary>Move the effect without changing how much of it there is: the local player's, placed from the view just before drawing.</summary>
        public void MoveTo(Vector3 seat)
        {
            if (_root != null && _root.activeSelf) _root.transform.position = seat;
        }

        public void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            _root = null;
            _fire = _smoke = _steam = null;
            _crackle = _hiss = null;
            _smoke01 = _fire01 = _steam01 = 0f;
        }

        private static bool Alive(ParticleSystem ps)
        {
            return ps != null && ps.particleCount > 0;
        }

        private static void SetRate(ParticleSystem ps, float rate)
        {
            if (ps == null) return;
            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = rate;
        }

        private static void SetVolume(AudioSource audio, float volume)
        {
            if (audio == null) return;
            audio.volume = volume;
            if (volume > 0.01f && !audio.isPlaying) audio.Play();
            else if (volume <= 0.01f && audio.isPlaying) audio.Stop();
        }

        private bool Build()
        {
            FindTemplates();
            if (_stove == null) return false;
            _root = new GameObject("PlayerModel scorch");
            _fire = CopyParticles(FireRef(_stove), SmokeRef(_stove), 0.13f, out _fireRate, 8f, 2f);
            // Flames stay on the pants. Left in world space they smear behind as the boat moves and the view rides
            // along with it; smoke and steam trailing off is right.
            if (_fire != null)
                foreach (var part in _fire.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = part.main;
                    main.simulationSpace = ParticleSystemSimulationSpace.Local;
                }
            _smoke = CopyParticles(SmokeRef(_stove), FireRef(_stove), 0.1f, out _smokeRate, 6f, 2f);
            _crackle = CopySound(_stove.GetComponent<AudioSource>());

            // Steam: a boiling kettle's own, or failing that the stove's smoke gone white.
            if (_kettle != null && BoilRef(_kettle) != null)
            {
                _steam = CopyParticles(BoilRef(_kettle), null, 0.12f, out _steamRate, 10f);
                _hiss = CopySound(_kettle.GetComponent<AudioSource>());
            }
            else if (SmokeRef(_stove) != null)
            {
                _steam = CopyParticles(SmokeRef(_stove), FireRef(_stove), 0.12f, out _steamRate, 6f);
                var main = _steam.main;
                main.startColor = new Color(0.95f, 0.95f, 0.95f, 0.6f);
            }
            // Rain on the fire makes more steam than the kettle ever does.
            _steamRate *= 2.5f;
            return _fire != null || _smoke != null;
        }

        private AudioSource CopySound(AudioSource sound)
        {
            if (sound == null || sound.clip == null) return null;
            var audio = _root.AddComponent<AudioSource>();
            audio.clip = sound.clip;
            audio.outputAudioMixerGroup = sound.outputAudioMixerGroup;
            audio.loop = true;
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.rolloffMode = sound.rolloffMode;
            audio.minDistance = Mathf.Max(1f, sound.minDistance);
            audio.maxDistance = Mathf.Max(12f, sound.maxDistance);
            audio.volume = 0f;
            return audio;
        }

        /// <summary>
        /// A copy of one particle system under the effect, stripped to the particles alone, and its full emission
        /// rate. Its own children stay (sparks and the like), except <paramref name="other"/> if it hangs underneath,
        /// which gets its own copy.
        /// </summary>
        private ParticleSystem CopyParticles(ParticleSystem source, ParticleSystem other, float radius, out float rate, float minRate, float speed = 1f)
        {
            rate = 0f;
            if (source == null) return null;
            var copy = Object.Instantiate(source.gameObject, _root.transform, false);
            copy.transform.localPosition = Vector3.zero;
            // Emitting the way it does where it came from, which stands upright on the deck.
            copy.transform.localRotation = source.transform.rotation;
            copy.transform.localScale = source.transform.lossyScale;
            foreach (var light in copy.GetComponentsInChildren<Light>(true)) Object.Destroy(light);
            foreach (var audio in copy.GetComponentsInChildren<AudioSource>(true)) Object.Destroy(audio);
            foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true)) Object.Destroy(behaviour);
            if (other != null && other.transform.IsChildOf(source.transform))
                foreach (var child in copy.GetComponentsInChildren<ParticleSystem>(true))
                    if (child.gameObject != copy && child.name == other.name) Object.Destroy(child.gameObject);
            var ps = copy.GetComponent<ParticleSystem>();
            // A stove's fire burns lazily; fire on someone's pants does not.
            foreach (var part in copy.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = part.main;
                main.simulationSpeed *= speed;
            }
            rate = Mathf.Max(minRate, source.emission.rateOverTime.constant) * speed;
            var shape = ps.shape;
            shape.radius = radius;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            ps.Play(true);
            return ps;
        }

        /// <summary>
        /// A loaded stove's fuel slot and a loaded kettle, looked for at most every few seconds until a stove turns up.
        /// One in the world is preferred to one only loaded as a prefab: a placed stove stands upright, so its fire
        /// points up.
        /// </summary>
        private static void FindTemplates()
        {
            if (_stove != null) return;
            if (Time.unscaledTime < _nextLook) return;
            _nextLook = Time.unscaledTime + 5f;
            _stove = Pick<StoveFuelTrigger>(t => FireRef(t) != null);
            _kettle = Pick<CookableFoodKettle>(k => BoilRef(k) != null);
            if (_stove != null)
                Plugin.Log.LogInfo($"[Scorch] using the fire from '{_stove.transform.root.name}'" +
                    (_kettle != null ? $" and the steam from '{_kettle.transform.root.name}'" : ", no kettle for steam"));
        }

        private static T Pick<T>(System.Func<T, bool> usable) where T : Component
        {
            T prefab = null;
            foreach (var c in Resources.FindObjectsOfTypeAll<T>())
            {
                if (c == null || !usable(c)) continue;
                if (c.gameObject.scene.IsValid()) return c;
                if (prefab == null) prefab = c;
            }
            return prefab;
        }
    }
}
