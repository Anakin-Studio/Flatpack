using System;
using System.Collections.Generic;
using Sandbox;

namespace Flatpack;

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
