using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Flatpack;

/// <summary>
/// One mesh LOD in a <see cref="FlatpackRenderer"/> chain: a model and the camera distance it takes over from.
/// </summary>
public sealed class FlatpackLodLevel
{
	/// <summary>
	/// The model to show in this band. Usually a lower-poly version of the source.
	/// </summary>
	[Property] public Model Model { get; set; }

	/// <summary>
	/// Camera distance where this level replaces the one before it, in world units.
	/// </summary>
	[Property, Range( 0, 20000 )] public float Distance { get; set; } = 1000.0f;
}

/// <summary>
/// Shows a model up close and its Flatpack impostor card far away.
/// </summary>
/// <remarks>
/// <para>
/// Put this next to a <see cref="ModelRenderer"/> and assign a baked <see cref="FlatpackImpostor"/>. The renderer
/// doesn't tick on its own: <see cref="FlatpackSystem"/> checks distances for every renderer in one batched,
/// time-sliced pass, so thousands of instances cost one loop instead of thousands of updates.
/// </para>
/// <para>
/// All cards made from the same impostor share one model and material, so the engine can instance them.
/// </para>
/// <para>
/// LODs: the source <c>.vmdl</c>'s own LODs keep working up close, because this component never touches the
/// <see cref="ModelRenderer"/> until the swap. For separate model files per detail level, fill <see cref="Levels"/>:
/// the renderer swaps the source's model by distance (with the same hysteresis), then hands over to the card past
/// the swap distance. With no <see cref="ModelRenderer"/> at all the card shows at every distance.
/// </para>
/// <para>
/// Alternative without this component: make the card the last LOD of the <c>.vmdl</c> (see <see cref="FlatpackImpostor"/>).
/// That gives you engine LOD switching for free, but no hysteresis and no cross-fade.
/// </para>
/// </remarks>
[Title( "Flatpack Renderer" ), Category( "Rendering" ), Icon( "view_in_ar" )]
public sealed class FlatpackRenderer : Component, Component.ExecuteInEditor
{
	/// <summary>
	/// What to draw.
	/// </summary>
	public enum DisplayMode
	{
		/// <summary>Model inside <see cref="EffectiveSwapDistance"/>, card beyond it.</summary>
		Auto,
		/// <summary>Always the real model.</summary>
		ForceModel,
		/// <summary>Always the card. Useful for checking a bake.</summary>
		ForceCard,
	}

	/// <summary>
	/// The baked impostor to show at distance. Left empty, the renderer uses any baked impostor whose source model
	/// matches the <see cref="Source"/> renderer's model, so adding the component after a bake needs no setup.
	/// </summary>
	[Property] public FlatpackImpostor Impostor { get; set; }

	/// <summary>
	/// The renderer for the real model. Found on this GameObject if left empty.
	/// </summary>
	[Property] public ModelRenderer Source { get; set; }

	/// <summary>
	/// Pick the swap distance from the object's size: the card takes over once the bounding sphere is about
	/// 1/<see cref="AutoDistanceFactor"/> of the distance across, roughly 5% of a 1080p screen's height at 70 degrees FOV.
	/// </summary>
	[Property] public bool AutoSwapDistance { get; set; } = true;

	/// <summary>
	/// Auto mode swaps at this many bounding-sphere radii from the camera.
	/// </summary>
	[Property, Range( 5, 100 ), ShowIf( nameof( AutoSwapDistance ), true )] public float AutoDistanceFactor { get; set; } = 30.0f;

	/// <summary>
	/// Camera distance where the card takes over, in world units, when <see cref="AutoSwapDistance"/> is off.
	/// </summary>
	[Property, Range( 0, 20000 ), ShowIf( nameof( AutoSwapDistance ), false )] public float SwapDistance { get; set; } = 2000.0f;

	/// <summary>
	/// The swap distance in use: <see cref="SwapDistance"/>, or the automatic one.
	/// </summary>
	public float EffectiveSwapDistance
	{
		get
		{
			if ( !AutoSwapDistance || Impostor is null )
				return SwapDistance;

			var scale = WorldScale;
			return Impostor.Radius * MathF.Max( scale.x, MathF.Max( scale.y, scale.z ) ) * AutoDistanceFactor;
		}
	}

	/// <summary>
	/// Width of the dead band around <see cref="SwapDistance"/>. The card appears past SwapDistance + Hysteresis / 2
	/// and the model returns inside SwapDistance - Hysteresis / 2, so a camera hovering at the boundary doesn't flicker.
	/// </summary>
	[Property, Range( 0, 2000 )] public float Hysteresis { get; set; } = 200.0f;

	/// <summary>
	/// Dither the card in and out over <see cref="FadeTime"/> instead of popping. The model stays visible under the
	/// card until the fade-in finishes, so the swap reads as a dissolve.
	/// </summary>
	[Property] public bool CrossFade { get; set; } = true;

	/// <summary>
	/// Seconds a cross-fade takes.
	/// </summary>
	[Property, Range( 0, 2 ), ShowIf( nameof( CrossFade ), true )] public float FadeTime { get; set; } = 0.35f;

	/// <summary>
	/// Override the distance check.
	/// </summary>
	[Property] public DisplayMode Mode { get; set; } = DisplayMode.Auto;

	/// <summary>
	/// Optional mesh LODs between the source model and the card, nearest first. Each replaces the source renderer's
	/// model past its <see cref="FlatpackLodLevel.Distance"/>. Distances past <see cref="SwapDistance"/> never show.
	/// </summary>
	[Property] public List<FlatpackLodLevel> Levels { get; set; } = new();

	/// <summary>
	/// Index into <see cref="Levels"/> currently on the source renderer, or -1 for the source's own model.
	/// </summary>
	public int CurrentLevel { get; private set; } = -1;

	/// <summary>
	/// True while the card is the visible representation (including while it fades in).
	/// </summary>
	public bool ShowingCard { get; private set; }

	SceneObject _card;
	Model _baseModel;      // the source renderer's own model, restored when the chain steps back to level -1
	FlatpackImpostor _countedImpostor;   // impostor this renderer's visible card is counted against in the system
	float _fade;           // 0 = card hidden, 1 = card fully shown
	float _fadeTarget;
	internal int SystemIndex = -1;

	protected override void OnEnabled()
	{
		Source ??= GameObject.Components.Get<ModelRenderer>();
		_baseModel = Source.IsValid() ? Source.Model : null;
		Impostor ??= FindImpostorFor( _baseModel );
		CurrentLevel = -1;
		Transform.OnTransformChanged += SyncTransform;

		Scene.GetSystem<FlatpackSystem>()?.Register( this );
	}

	protected override void OnDisabled()
	{
		Transform.OnTransformChanged -= SyncTransform;
		Scene.GetSystem<FlatpackSystem>()?.Unregister( this );

		DestroyCard();
		SetLevel( -1 );
		SetSourceVisible( true );
		ShowingCard = false;
		_fade = _fadeTarget = 0.0f;
	}

	protected override void OnValidate()
	{
		// An edited impostor or a changed mode should show up straight away in the editor.
		DestroyCard();
		SetLevel( -1 );
		ShowingCard = false;
		_fade = _fadeTarget = 0.0f;
		SetSourceVisible( true );
	}

	/// <summary>
	/// Called by <see cref="FlatpackSystem"/> with the squared distance from the camera to this object.
	/// Decides which representation should be showing; the fade itself runs in <see cref="TickFade"/>.
	/// </summary>
	internal bool Evaluate( float distanceSquared )
	{
		UpdateLevel( distanceSquared );

		var wantCard = Mode switch
		{
			DisplayMode.ForceCard => true,
			DisplayMode.ForceModel => false,
			_ => WantCardAt( distanceSquared ),
		};

		// Card-only setup: nothing else to show.
		if ( !Source.IsValid() && Mode != DisplayMode.ForceModel )
			wantCard = true;

		if ( wantCard && Impostor?.Material is null )
			wantCard = false;

		if ( wantCard == ShowingCard )
			return false;

		ShowingCard = wantCard;
		_fadeTarget = wantCard ? 1.0f : 0.0f;

		if ( wantCard )
			EnsureCard();

		if ( !CrossFade || FadeTime <= 0.0f )
			_fade = _fadeTarget;

		Apply();

		// The system keeps calling TickFade while this returns true.
		return _fade != _fadeTarget;
	}

	/// <summary>
	/// Step the mesh LOD chain toward the level for this distance, one band edge at a time, each with the same
	/// hysteresis band as the card swap.
	/// </summary>
	void UpdateLevel( float distanceSquared )
	{
		if ( Levels is null || Levels.Count == 0 || !Source.IsValid() || Mode != DisplayMode.Auto )
			return;

		var level = Math.Min( CurrentLevel, Levels.Count - 1 );
		var half = Hysteresis * 0.5f;

		while ( level + 1 < Levels.Count && Beyond( distanceSquared, Levels[level + 1].Distance + half ) )
			level++;

		while ( level >= 0 && !Beyond( distanceSquared, Levels[level].Distance - half ) )
			level--;

		SetLevel( level );
	}

	static bool Beyond( float distanceSquared, float distance )
	{
		distance = MathF.Max( distance, 0.0f );
		return distanceSquared > distance * distance;
	}

	void SetLevel( int level )
	{
		if ( level == CurrentLevel || !Source.IsValid() )
			return;

		CurrentLevel = level;
		var model = level >= 0 ? Levels[level].Model : _baseModel;
		if ( model is not null )
			Source.Model = model;

		// A new model can mean a new scene object, which starts visible.
		SetSourceVisible( !ShowingCard || _fade < 1.0f );
	}

	/// <summary>
	/// The first baked impostor whose source is <paramref name="model"/>, or null.
	/// </summary>
	public static FlatpackImpostor FindImpostorFor( Model model )
	{
		if ( model is null )
			return null;

		return ResourceLibrary.GetAll<FlatpackImpostor>()
			.FirstOrDefault( x => x.SourceModel is not null && x.SourceModel.ResourcePath == model.ResourcePath );
	}

	bool WantCardAt( float distanceSquared )
	{
		// Hysteresis: the threshold depends on which side we're already on.
		var swap = EffectiveSwapDistance;
		var threshold = ShowingCard
			? swap - Hysteresis * 0.5f
			: swap + Hysteresis * 0.5f;

		threshold = MathF.Max( threshold, 0.0f );
		return distanceSquared > threshold * threshold;
	}

	/// <summary>
	/// Advances a running cross-fade. Returns false when it has finished.
	/// </summary>
	internal bool TickFade( float delta )
	{
		var step = FadeTime > 0.0f ? delta / FadeTime : 1.0f;
		_fade = _fadeTarget > _fade
			? MathF.Min( _fade + step, _fadeTarget )
			: MathF.Max( _fade - step, _fadeTarget );

		Apply();
		return _fade != _fadeTarget;
	}

	void Apply()
	{
		if ( _card.IsValid() )
		{
			_card.RenderingEnabled = _fade > 0.0f;
			CountCard( _fade > 0.0f ? Impostor : null );

			// 1 draws every pixel; anything lower dithers the card out (see flatpack_impostor.shader).
			_card.Attributes.Set( "FlatpackFade", _fade );
		}

		// The model stays up until the card has fully faded in, and comes back the moment we head toward it.
		SetSourceVisible( !ShowingCard || _fade < 1.0f );
	}

	void SetSourceVisible( bool visible )
	{
		if ( Source.IsValid() && Source.SceneObject.IsValid() )
			Source.SceneObject.RenderingEnabled = visible && Source.Enabled;
	}

	void EnsureCard()
	{
		if ( _card.IsValid() || Impostor is null )
			return;

		_card = new SceneObject( Scene.SceneWorld, Impostor.CardModel, WorldTransform );
		_card.Tags.SetFrom( GameObject.Tags );
		_card.RenderingEnabled = false;
	}

	/// <summary>
	/// Keep <see cref="FlatpackSystem"/>'s count of visible cards per impostor current, so it streams their atlases in.
	/// </summary>
	void CountCard( FlatpackImpostor visibleImpostor )
	{
		if ( visibleImpostor == _countedImpostor )
			return;

		var system = Scene?.GetSystem<FlatpackSystem>();
		system?.CardVisibilityChanged( _countedImpostor, false );
		system?.CardVisibilityChanged( visibleImpostor, true );
		_countedImpostor = visibleImpostor;
	}

	void DestroyCard()
	{
		CountCard( null );
		_card?.Delete();
		_card = null;
	}

	void SyncTransform()
	{
		if ( _card.IsValid() )
			_card.Transform = WorldTransform;
	}
}
