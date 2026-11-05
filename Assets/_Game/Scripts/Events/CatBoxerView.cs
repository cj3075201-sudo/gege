using TMPro;
using UnityEngine;

namespace DeliveryRider
{
    // Primitive-only presentation, driven by the duel. No combat state is written here.
    public sealed class CatBoxerView : MonoBehaviour
    {
        [SerializeField] private Transform model, leftArm, rightArm, leftEar, rightEar, tellRing;
        [SerializeField] private TMP_Text caption;
        private string lastCaption;
        private Renderer ringRenderer;
        private MaterialPropertyBlock ringProperties;
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private CatBoxerHazard.Attack lastAttack;
        private int lastHealth = -1, lastTaken = -1;
        public void Show(CatBoxerHazard cat, Vector3 player, float remaining, float elapsed, float leaveSeconds)
        {
            var facing = Vector3.ProjectOnPlane(player - transform.position, Vector3.up);
            if (facing.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(facing);
            bool tell = cat.State == CatBoxerHazard.Mode.Bout && cat.Phase == CatBoxerHazard.Beat.Tell;
            bool open = cat.State == CatBoxerHazard.Mode.Bout && cat.Phase == CatBoxerHazard.Beat.Open;
            bool ko = cat.State == CatBoxerHazard.Mode.Knockout;
            bool leaving = cat.State == CatBoxerHazard.Mode.Leaving;
            if (model != null)
            {
                model.localScale = new Vector3(1.45f, tell && cat.CurrentAttack == CatBoxerHazard.Attack.Uppercut ? 0.899f : 1.45f, 1.45f);
                model.localPosition = Vector3.up * (ko ? 0.28f : Mathf.Abs(Mathf.Sin(Time.time * 9f)) * 0.05f);
                model.localRotation = Quaternion.Euler(ko ? 83f : leaving ? -31.5f * Mathf.Sin(Mathf.Clamp01(elapsed / leaveSeconds) * Mathf.PI)
                    : open ? Mathf.Sin(Time.time * 15f) * 9f : 0f, 0f, 0f);
            }
            float swing = Mathf.Sin(Time.time * 11f);
            if (leftArm != null) leftArm.localRotation = Quaternion.Euler(ko || leaving ? -28f : open ? -75f : -75f - Mathf.Max(0f, swing) * 34f, tell && cat.CurrentAttack == CatBoxerHazard.Attack.Jab ? -35f : 0f, 0f);
            if (rightArm != null) rightArm.localRotation = Quaternion.Euler(ko || leaving ? -28f : tell && cat.CurrentAttack == CatBoxerHazard.Attack.Hook ? -166f : open ? -23f : -75f - Mathf.Max(0f, -swing) * 34f, 0f, 0f);
            if (leftEar != null) leftEar.localRotation = Quaternion.Euler(tell && cat.CurrentAttack == CatBoxerHazard.Attack.Jab ? -63f : 0f, 0f, 0f);
            if (rightEar != null) rightEar.localRotation = leftEar != null ? leftEar.localRotation : Quaternion.identity;
            if (tellRing != null)
            {
                tellRing.gameObject.SetActive(tell);
                float diameter = cat.CurrentAttack == CatBoxerHazard.Attack.Hook ? 5.2f : cat.CurrentAttack == CatBoxerHazard.Attack.Uppercut ? 3.4f : 2.6f;
                tellRing.localScale = new Vector3(diameter, 0.015f, diameter);
                if (tell && (ringProperties == null || lastAttack != cat.CurrentAttack))
                {
                    if (ringRenderer == null) ringRenderer = tellRing.GetComponent<Renderer>();
                    ringProperties = ringProperties ?? new MaterialPropertyBlock();
                    var color = cat.CurrentAttack == CatBoxerHazard.Attack.Jab ? new Color(.56f,.84f,1f) : cat.CurrentAttack == CatBoxerHazard.Attack.Hook ? new Color(1f,.7f,.28f) : new Color(1f,.35f,.43f);
                    ringProperties.SetColor(BaseColor, color); ringRenderer.SetPropertyBlock(ringProperties); lastAttack = cat.CurrentAttack;
                }
            }
            string text = ko ? "K.O.!" : leaving ? "야옹!" : cat.State == CatBoxerHazard.Mode.Pal ? "함께 배달!"
                : tell ? cat.CurrentAttack == CatBoxerHazard.Attack.Jab ? "잽 — 옆으로!" : cat.CurrentAttack == CatBoxerHazard.Attack.Hook ? "훅 — 점프!" : "어퍼컷 — 물러나!"
                : open ? "지금! F" : "고양이 복서";
            if (caption != null)
            {
                if (text != lastCaption || lastHealth != cat.Health || lastTaken != cat.TakenHits)
                {
                    caption.text = cat.State == CatBoxerHazard.Mode.Bout ? text + "\nHP " + cat.Health + " / " + cat.TakenHits : text;
                    lastCaption = text; lastHealth = cat.Health; lastTaken = cat.TakenHits;
                }
                // Face the rider; the game camera stays behind them.
                caption.transform.rotation = Quaternion.LookRotation(-transform.forward);
            }
        }
    }
}
