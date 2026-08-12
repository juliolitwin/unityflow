using System;

namespace UnityFlow
{
    /// <summary>
    /// Marks a method as callable from a flow step.
    ///
    /// This attribute is the ONLY part of UnityFlow that ships in a player build. It is a pure
    /// marker with no dependencies, so annotating game code never drags the test runner into a
    /// release build.
    ///
    /// Both instance and static methods are supported:
    /// <list type="bullet">
    /// <item>
    ///   An INSTANCE method on a <c>MonoBehaviour</c> solves the reference problem by
    ///   construction — <c>this</c> is the object the step acts on. The runner locates the
    ///   component in the scene (and <c>on:</c> disambiguates when several exist).
    /// </item>
    /// <item>
    ///   A STATIC method is right for cross-cutting concerns. Declare Component/GameObject
    ///   parameters and the binder resolves them from the scene.
    /// </item>
    /// </list>
    ///
    /// Return types <c>void</c>, <c>IEnumerator</c> and <c>Task</c> are all supported. Returning
    /// <c>IEnumerator</c> matters: the runner yields on it, so a scene load or animation is awaited
    /// properly instead of degrading into a guessed <c>wait: 2s</c>. A non-void return value is
    /// readable from a flow via <c>assert: { call: &lt;name&gt; }</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// public class PlayerWallet : MonoBehaviour
    /// {
    ///     [SerializeField] int money;
    ///
    ///     [FlowCommand("giveCoins")]
    ///     public void GiveCoins(int amount) =&gt; money += amount;
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class FlowCommandAttribute : Attribute
    {
        /// <summary>Name used to invoke this command from YAML. Must be unique in the project.</summary>
        public string Name { get; }

        /// <summary>Optional human-readable description, surfaced by <c>unityflow commands</c>.</summary>
        public string Description { get; set; }

        public FlowCommandAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A flow command name cannot be null or blank.", nameof(name));

            Name = name;
        }
    }
}
