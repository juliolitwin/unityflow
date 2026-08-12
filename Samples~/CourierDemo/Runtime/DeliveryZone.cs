using UnityEngine;

namespace UnityFlow.Samples.Courier
{
    /// <summary>
    /// Where parcels are handed over.
    ///
    /// A pure marker with no members: the collider is the whole behaviour, and
    /// <see cref="CourierPlayer"/> asks "is this the drop-off?" by TYPE rather than by name, so
    /// renaming the object in the hierarchy cannot break delivery.
    ///
    /// It gets a file of its own, like every other MonoBehaviour here. Unity gives the class that
    /// matches the file name the script asset's main id and hands every other class in the same
    /// file a derived one; a component of such a class serializes into a scene but its script does
    /// not resolve when the scene is loaded at run time — measured here as a delivery zone that was
    /// present in the Inspector and null to GetComponent in play mode.
    /// </summary>
    public sealed class DeliveryZone : MonoBehaviour
    {
    }
}
