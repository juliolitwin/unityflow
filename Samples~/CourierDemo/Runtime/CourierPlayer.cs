using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace UnityFlow.Samples.Courier
{
    /// <summary>
    /// The capsule: movement, and every contact the game has with the props.
    ///
    /// All three trigger reactions live here rather than on the props, because only one of the two
    /// colliders in a contact has the Rigidbody and putting the reactions on the moving side keeps
    /// "what happens when the courier touches X" readable in one place.
    ///
    /// Movement runs in <c>FixedUpdate</c> through <see cref="Rigidbody.MovePosition"/>, which is
    /// what makes a flow's <c>press: { key: w, duration: 2.4s }</c> mean a fixed DISTANCE: the step
    /// holds the key for a wall-clock duration, and only a physics-timed move turns that into the
    /// same travel on a fast editor and a slow one.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class CourierPlayer : MonoBehaviour
    {
        [SerializeField] private CourierGame m_Game;
        [SerializeField] private CourierInventory m_Cargo;
        [SerializeField] private PlayerHealth m_Health;
        [SerializeField] private ScoreKeeper m_Score;

        [SerializeField] private float m_Speed = 6f;
        [SerializeField] private float m_ArenaHalfSize = 9.5f;
        [SerializeField] private float m_HurtCooldown = 1f;

        private Rigidbody m_Body;
        private Vector3 m_Home;
        private int m_ZoneOverlaps;
        private float m_HurtLockout;

        /// <summary>Whether the courier is standing in the drop-off.</summary>
        public bool inZone => m_ZoneOverlaps > 0;

        private void Awake()
        {
            m_Body = GetComponent<Rigidbody>();
            m_Home = transform.position;
        }

        /// <summary>Put the courier back on the start mark for a new run.</summary>
        public void BeginRun()
        {
            m_Body.position = m_Home;
            transform.position = m_Home;
            m_ZoneOverlaps = 0;
            m_HurtLockout = 0f;
        }

        private void Update()
        {
            if (m_Game.phase != CourierPhase.Playing)
                return;

            m_HurtLockout = Mathf.Max(0f, m_HurtLockout - Time.deltaTime);

            // Delivery is a STATE, not an entry event: walking in empty-handed and then being handed
            // a parcel by a flow command has to hand it over too.
            if (m_ZoneOverlaps > 0 && m_Cargo.count > 0)
                m_Score.Deliver(m_Cargo.count, m_Cargo.TakeAll());
        }

        private void FixedUpdate()
        {
            if (m_Game.phase != CourierPhase.Playing)
                return;

            var keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            var move = new Vector3(
                Axis(keyboard.dKey, keyboard.rightArrowKey) - Axis(keyboard.aKey, keyboard.leftArrowKey),
                0f,
                Axis(keyboard.wKey, keyboard.upArrowKey) - Axis(keyboard.sKey, keyboard.downArrowKey));

            if (move.sqrMagnitude > 1f)
                move.Normalize();

            var next = m_Body.position + move * (m_Speed * Time.fixedDeltaTime);

            // Clamping INSIDE the drop-off's far edge means a flow that holds a key too long still
            // ends up somewhere the game can act on, instead of drifting off the plane.
            m_Body.MovePosition(new Vector3(
                Mathf.Clamp(next.x, -m_ArenaHalfSize, m_ArenaHalfSize),
                m_Home.y,
                Mathf.Clamp(next.z, -m_ArenaHalfSize, m_ArenaHalfSize)));
        }

        private void OnTriggerEnter(Collider other)
        {
            if (m_Game.phase != CourierPhase.Playing)
                return;

            var parcel = other.GetComponent<Parcel>();
            if (parcel != null)
            {
                if (m_Cargo.TryCarry(parcel.label, parcel.value))
                    parcel.gameObject.SetActive(false);

                return;
            }

            var hazard = other.GetComponent<Hazard>();
            if (hazard != null)
            {
                Hurt(hazard.damage);
                return;
            }

            if (other.GetComponent<DeliveryZone>() != null)
                m_ZoneOverlaps++;
        }

        private void OnTriggerExit(Collider other)
        {
            if (other.GetComponent<DeliveryZone>() != null)
                m_ZoneOverlaps = Mathf.Max(0, m_ZoneOverlaps - 1);
        }

        private void Hurt(int amount)
        {
            if (m_HurtLockout > 0f)
                return;

            m_HurtLockout = m_HurtCooldown;
            m_Health.Hurt(amount);
        }

        /// <summary>WASD and the arrow keys drive the same axis, so both halves of the brief work.</summary>
        private float Axis(KeyControl letter, KeyControl arrow) => letter.isPressed || arrow.isPressed ? 1f : 0f;
    }
}
