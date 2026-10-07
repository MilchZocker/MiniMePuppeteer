using System.Reflection;
using MelonLoader;
using MiniMePuppeteer;

[assembly: AssemblyTitle("MiniMePuppeteer")]
[assembly: AssemblyDescription("Allows using the Remote MiniMe as a puppeteer controller for other players.")]
[assembly: AssemblyCompany("CVRModding")]
[assembly: AssemblyProduct("MiniMePuppeteer")]
[assembly: AssemblyVersion("1.0.0")]
[assembly: AssemblyFileVersion("1.0.0")]

[assembly: MelonInfo(typeof(MiniMePuppeteerMod), "MiniMePuppeteer", "1.0.0", "Author")]
[assembly: MelonGame("ChilloutVR", "ChilloutVR")]
[assembly: MelonGame(null, "ChilloutVR")]
[assembly: MelonAdditionalDependencies("MiniMe++")]
[assembly: MelonOptionalDependencies("BTKUILib", "ml_prm")]
