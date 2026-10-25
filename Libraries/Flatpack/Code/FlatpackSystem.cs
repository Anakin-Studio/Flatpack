using System;
using System.Collections.Generic;
using Sandbox;

namespace Flatpack;

/// <summary>
/// How hard Flatpack works, for a game's graphics settings: high quality presets turn it off, low ones lean on it.
/// Set <see cref="FlatpackSystem.Level"/>; every Flatpack Renderer follows it within a few frames, nothing re-bakes.
/// </summary>
public enum FlatpackLevel
{
	/// <summary>Real models everywhere; cards never show (a game's High or Ultra preset).</summary>
	Off,

	/// <summary>Swap where the bake was measured to look like the model (the default; a game's Medium).</summary>
	Balanced,

	/// <summary>Swap at 0.6 times the distance, from 3 bounding radii (a game's Low).</summary>
	Performance,

	/// <summary>Swap at 0.35 times the distance, from 1.5 bounding radii (a game's lowest preset or weak hardware).</summary>
	Maximum,
}

/// <summary>
/// Runs the model/card distance check for every <see cref="FlatpackRenderer"/> in the scene.
/// </summary>
/// <remarks>
/// One loop per frame instead of one Update per renderer. The loop is time-sliced: each frame checks at most
/// <see cref="RenderersPerFrame"/> renderers, round-robin, so a forest of 5000 trees spreads its checks over a few
/// frames. Renderers in the middle of a cross-fade are ticked every frame until they finish.
/// </remarks>
public sealed class FlatpackSystem : GameObjectSystem<FlatpackSystem>
{
	/// <summary>
	/// Global level for every Flatpack Renderer, meant to be driven by a game's graphics settings, for example
	/// <c>FlatpackSystem.Level = FlatpackLevel.Performance;</c> on the Low preset. Applies to automatic and manual swap
	/// distances alike; Force Model and Force Card modes ignore it.
	/// </summary>
	public static FlatpackLevel Level { get; set; } = FlatpackLevel.Balanced;

	/// <summary>
	/// What <see cref="Level"/> does to a swap distance: a multiplier, and the closest swap allowed in bounding radii
	/// (0 = the renderer's own minimum). <see cref="FlatpackLevel.Off"/> pushes the swap out to infinity.
	/// </summary>
	public static (float Scale, float MinimumRadii) LevelScaling => Level switch
	{
		FlatpackLevel.Off => (float.PositiveInfinity, 0.0f),
		FlatpackLevel.Performance => (0.6f, 3.0f),
		FlatpackLevel.Maximum => (0.35f, 1.5f),
		_ => (1.0f, 0.0f),
	};

	/// <summary>
	/// Distance checks per frame. Lower spreads the work thinner but makes swaps lag behind a fast camera.
	/// </summary>
	public int RenderersPerFrame { get; set; } = 2048;

	/// <summary>
	/// Number of registered renderers.
	/// </summary>
	public int Count => _renderers.Count;

	readonly List<FlatpackRenderer> _renderers = new();
	readonly List<FlatpackRenderer> _fading = new();
	readonly Dictionary<FlatpackImpostor, int> _visibleCards = new();
	int _cursor;

	public FlatpackSystem( Scene scene ) : base( scene )
	{
		// After the scene update, so this frame's camera and object movement are already in.
		Listen( Stage.FinishUpdate, 100, Tick, "Flatpack.DistancePass" );
	}

	internal void Register( FlatpackRenderer renderer )
	{
		if ( renderer.SystemIndex >= 0 )
			return;

		renderer.SystemIndex = _renderers.Count;
		_renderers.Add( renderer );
	}

	internal void Unregister( FlatpackRenderer renderer )
	{
		var index = renderer.SystemIndex;
		if ( index < 0 || index >= _renderers.Count || _renderers[index] != renderer )
			return;

		// Swap-remove keeps this O(1); the moved renderer takes over the freed slot.
		var last = _renderers[^1];
		_renderers[index] = last;
		last.SystemIndex = index;
		_renderers.RemoveAt( _renderers.Count - 1 );

		renderer.SystemIndex = -1;
		_fading.Remove( renderer );
	}

	/// <summary>
	/// Reference count of visible cards per impostor, kept by <see cref="FlatpackRenderer"/>, so the tick can keep
	/// those atlases streamed in without walking every renderer.
	/// </summary>
	internal void CardVisibilityChanged( FlatpackImpostor impostor, bool visible )
	{
		if ( impostor is null )
			return;

		_visibleCards.TryGetValue( impostor, out var count );
		count += visible ? 1 : -1;

		if ( count > 0 )
			_visibleCards[impostor] = count;
		else
			_visibleCards.Remove( impostor );
	}

	void Tick()
	{
		foreach ( var impostor in _visibleCards.Keys )
			impostor.MarkAtlasesUsed();

		if ( _renderers.Count == 0 )
			return;

		var camera = Scene.Camera;
		if ( !camera.IsValid() )
			return;

		var cameraPosition = camera.WorldPosition;
		var count = Math.Min( RenderersPerFrame, _renderers.Count );

		for ( var i = 0; i < count; i++ )
		{
			if ( _cursor >= _renderers.Count )
				_cursor = 0;

			var renderer = _renderers[_cursor++];
			var distanceSquared = cameraPosition.DistanceSquared( renderer.WorldPosition );

			if ( renderer.Evaluate( distanceSquared ) && !_fading.Contains( renderer ) )
				_fading.Add( renderer );
		}

		if ( _fading.Count == 0 )
			return;

		var delta = Time.Delta;
		for ( var i = _fading.Count - 1; i >= 0; i-- )
		{
			if ( !_fading[i].TickFade( delta ) )
				_fading.RemoveAt( i );
		}
	}
}
