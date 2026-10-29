namespace Inventory.Tests
{
    using System;

    /// <summary>
    /// Assertions.
    /// </summary>
    public static class Check
    {
        /// <summary>
        /// Fail unless the values are equal.
        /// </summary>
        /// <typeparam name="T">Value type.</typeparam>
        /// <param name="expected">Expected value.</param>
        /// <param name="actual">Actual value.</param>
        /// <exception cref="Exception">Thrown when the values differ.</exception>
        public static void Equal<T>(T expected, T actual)
        {
            if (!Object.Equals(expected, actual)) throw new Exception("Expected " + expected + " but was " + actual + ".");
        }

        /// <summary>
        /// Fail unless the condition holds.
        /// </summary>
        /// <param name="condition">Condition.</param>
        /// <param name="message">Failure message.</param>
        /// <exception cref="Exception">Thrown when the condition is false.</exception>
        public static void True(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        /// <summary>
        /// Fail unless the action throws an exception of type T.
        /// </summary>
        /// <typeparam name="T">Expected exception type.</typeparam>
        /// <param name="action">Action.</param>
        /// <exception cref="Exception">Thrown when the action does not throw T.</exception>
        public static void Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new Exception("Expected " + typeof(T).Name + " but got " + ex.GetType().Name + ".");
            }

            throw new Exception("Expected " + typeof(T).Name + " but nothing was thrown.");
        }
    }
}
