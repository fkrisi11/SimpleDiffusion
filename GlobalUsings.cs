// The Models and Services namespaces hold the app's core data types and services, referenced from
// nearly every component and code-behind. They live here as global usings (rather than a per-file
// using in dozens of .cs files) so the folder/namespace split stays an organisational detail, not
// friction. Razor files get the same two usings via Components/_Imports.razor.
global using SimpleDiffusion.Components.Models;
global using SimpleDiffusion.Components.Services;
