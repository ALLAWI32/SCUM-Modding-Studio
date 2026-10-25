namespace ScumStudio.App.ViewModels;

/// <summary>
/// A material the Textures panel lists: a paint slot of a vehicle or weapon, or any material a preview part of an item, a
/// magazine or a round wears (those are no paint, so the Materials panel has nothing of them).
/// </summary>
/// <param name="MaterialPath">The stock material's object path (the preview parts drawn with it wear it).</param>
/// <param name="Name">The readable name the picker shows.</param>
public sealed record TextureMaterialViewModel(string MaterialPath, string Name);
