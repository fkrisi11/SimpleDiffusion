namespace SimpleDiffusion.Components.Models
{
    /// <summary>
    /// A named, reusable block of tags the user can save and later append to the prompt (on a new
    /// line at the end) without disturbing the rest of the prompt.
    /// </summary>
    public class TagGroup
    {
        public string Name { get; set; } = "";
        public string Tags { get; set; } = "";
    }
}
