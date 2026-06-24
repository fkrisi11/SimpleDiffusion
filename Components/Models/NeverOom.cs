using Microsoft.Extensions.Configuration;

namespace SimpleDiffusion.Components.Models
{
    /// <summary>
    /// reForge/Forge "NeverOOM" memory-safety integration. It's an always-on server script that runs
    /// per generation; we surface it by adding an entry to the request's <c>alwayson_scripts</c>,
    /// keyed by the script's title. The script (reForge <c>forge_never_oom.py</c>) takes four
    /// positional args: <c>[unet_enabled, vae_enabled, encoder_tile_size, decoder_tile_size]</c>.
    /// <list type="bullet">
    ///   <item>UNet — always maximize model offload (much lower VRAM on the diffusion pass, slower).</item>
    ///   <item>VAE  — always tiled decode/encode (avoids OOM on large images); the tile sizes below
    ///         control how finely it tiles (smaller = less VRAM, more seams/slower).</item>
    /// </list>
    /// Only meaningful for txt2img / img2img (the UNet + VAE paths). The extras upscaler
    /// (<c>sdapi/v1/extra-single-image</c>) uses neither, so NeverOOM doesn't apply there.
    /// </summary>
    public static class NeverOom
    {
        /// <summary>The reForge script title that keys the entry in <c>alwayson_scripts</c>. If a
        /// future reForge build renames the script, change this single string.</summary>
        public const string ScriptTitle = "Never OOM Integrated";

        // Tile-size ranges/steps mirror the reForge sliders exactly (forge_never_oom.py).
        public const int EncoderTileMin = 256, EncoderTileMax = 4096;
        public const int DecoderTileMin = 48, DecoderTileMax = 512;
        public const int TileStep = 16;

        // Defaults when unset. reForge picks these from VRAM at runtime; we can't, so use mid-range
        // values that bias slightly toward memory safety. Users adjust via the sliders.
        public const int DefaultEncoderTile = 1536;
        public const int DefaultDecoderTile = 96;

        public static int ClampEncoder(int v) => Math.Clamp(v, EncoderTileMin, EncoderTileMax);
        public static int ClampDecoder(int v) => Math.Clamp(v, DecoderTileMin, DecoderTileMax);

        /// <summary>Read + clamp the configured encoder tile size (settings key <c>NeverOomEncoderTile</c>).</summary>
        public static int EncoderTile(IConfiguration cfg) => ClampEncoder(ReadInt(cfg, "NeverOomEncoderTile", DefaultEncoderTile));

        /// <summary>Read + clamp the configured decoder tile size (settings key <c>NeverOomDecoderTile</c>).</summary>
        public static int DecoderTile(IConfiguration cfg) => ClampDecoder(ReadInt(cfg, "NeverOomDecoderTile", DefaultDecoderTile));

        private static int ReadInt(IConfiguration cfg, string key, int fallback) =>
            int.TryParse(cfg[key], out var v) ? v : fallback;

        /// <summary>
        /// Return <paramref name="alwaysOn"/> with the NeverOOM entry merged in. <paramref name="alwaysOn"/>
        /// may be null or the ControlNet dictionary; a new object is returned so the caller's (possibly
        /// shared/cloned) scripts object isn't mutated. When both modes are off the input is returned
        /// unchanged, so non-NeverOOM requests serialize exactly as before. The tile sizes are always
        /// sent (the script reads all four args); they only take effect when <paramref name="vae"/> is on.
        /// </summary>
        public static object? Merge(object? alwaysOn, bool unet, bool vae, int encoderTile, int decoderTile)
        {
            if (!unet && !vae) return alwaysOn;

            var dict = alwaysOn is IDictionary<string, object> existing
                ? new Dictionary<string, object>(existing)
                : new Dictionary<string, object>();

            // args are positional, matching the script: [unet_enabled, vae_enabled, encoder_tile, decoder_tile].
            dict[ScriptTitle] = new { args = new object[] { unet, vae, ClampEncoder(encoderTile), ClampDecoder(decoderTile) } };
            return dict;
        }
    }
}
