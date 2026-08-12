using UnityEngine;

namespace UnityFlow.Samples.Courier
{
    /// <summary>
    /// The parcels authored under this object, and the one thing the round needs from them: putting
    /// them all back.
    ///
    /// A picked-up parcel is deactivated rather than destroyed, so a replay restores the street
    /// without instantiating anything and the scene keeps showing every parcel it was authored with.
    /// </summary>
    public sealed class ParcelField : MonoBehaviour
    {
        private Parcel[] m_Parcels;

        /// <summary>Parcels still lying in the street. Zero with an empty inventory ends the run.</summary>
        public int available
        {
            get
            {
                var live = 0;
                for (var i = 0; i < m_Parcels.Length; i++)
                {
                    if (m_Parcels[i].gameObject.activeSelf)
                        live++;
                }

                return live;
            }
        }

        private void Awake() => m_Parcels = GetComponentsInChildren<Parcel>(true);

        /// <summary>Put every parcel back on the street.</summary>
        public void BeginRun()
        {
            for (var i = 0; i < m_Parcels.Length; i++)
                m_Parcels[i].gameObject.SetActive(true);
        }
    }
}
