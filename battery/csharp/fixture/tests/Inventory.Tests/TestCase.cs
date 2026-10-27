namespace Inventory.Tests
{
    using System;

    /// <summary>
    /// A named test.
    /// </summary>
    public class TestCase
    {
        /// <summary>
        /// Test name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Test body; throws to fail.
        /// </summary>
        public Action Body { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="name">Test name.</param>
        /// <param name="body">Test body.</param>
        /// <exception cref="ArgumentNullException">Thrown when name or body is null.</exception>
        public TestCase(string name, Action body)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Body = body ?? throw new ArgumentNullException(nameof(body));
        }
    }
}
