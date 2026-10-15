namespace Armada.Server
{
    using System;
    using System.Text.Json;

    /// <summary>
    /// Records whether a persona update body carries DefaultCaptainId. Deserialize the same request body into this
    /// class: the setter runs only when the property is present in the JSON (including an explicit null), so an
    /// editor that does not show the default captain leaves it unchanged, while one that sends null clears it.
    /// </summary>
    public class PersonaUpdateFieldPresence
    {
        /// <summary>Default captain id (presence only).</summary>
        public string? DefaultCaptainId { get => null; set => HasDefaultCaptainId = true; }

        /// <summary>Whether the body carried DefaultCaptainId.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasDefaultCaptainId { get; private set; } = false;

        /// <summary>
        /// Read which persona fields a JSON body carries.
        /// </summary>
        /// <param name="json">Request body.</param>
        /// <param name="options">Serializer options used for the persona body.</param>
        /// <returns>The presence record; an empty body reports no fields.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
        public static PersonaUpdateFieldPresence FromJson(string? json, JsonSerializerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (String.IsNullOrWhiteSpace(json)) return new PersonaUpdateFieldPresence();
            return JsonSerializer.Deserialize<PersonaUpdateFieldPresence>(json, options) ?? new PersonaUpdateFieldPresence();
        }
    }
}
