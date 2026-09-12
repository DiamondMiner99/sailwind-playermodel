using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Drives the pause menu and the character screen each frame. Separate from the plugin's other work so a
    /// throw in one of them cannot stop the body animating, and vice versa.
    /// </summary>
    public class MenuDriver : MonoBehaviour
    {
        private void Update()
        {
            try
            {
                ModPauseMenu.Tick();
                CharacterScreen.Tick();
            }
            catch (System.Exception e) { Plugin.Log.LogError("[MenuDriver] update failed: " + e); }
        }

        private void LateUpdate()
        {
            // MUST be LateUpdate, not Update. See ModPauseMenu.LatePin for why a frame of camera lag shows.
            try { ModPauseMenu.LatePin(); }
            catch (System.Exception e) { Plugin.Log.LogError("[MenuDriver] late pin failed: " + e); }
        }

        private void OnGUI()
        {
            try { CharacterScreen.Draw(); }
            catch (System.Exception e) { Plugin.Log.LogError("[MenuDriver] draw failed: " + e); }
        }
    }
}
