﻿/* 
 https://www.harrygodden.com

 Networking Model Information:
	
	This implementation of 8 ball is based around passing ownership between clients who are
	playing the game. A player is 'registered' into the game when they have ownership of one
	of the two player 'totems'. In this implementation the totems are the pool cues themselves.

	When a turn ends, the player who is currently playing will pack information into the 
	networking string that the turn has been transferred, and once the remote client who is
	associated with the opposite cue recieves the update, they will take ownership of the main
	script.

	The local player will have a 'permit' to shoot when it is their turn, which allows them
	to interact with the physics world. As soon as the cue ball is shot, the script calculates
	and compresses the necessery velocities and positions of the balls, and 1. sends that out
	to remote clients, and 2. decodes it the same way themselves. So effectively all players
	end up watching the exact same simulation at very close to the same time. In testing this
	was immediate as it could be with a GB -> USA connection.

 Information about the data:

	- Data is transfered using 1 Udon Synced string which is 21 wchar in length
	- Critical game states are packed into a bitmask at #19
	- Floating point positions are encoded/decoded as follows:
		Encode:
			Divide the value by the expected maximum range
			Multiply that by signed short max value ~32k
			Add signed short max
			Cast to ushort
		Decode:
			Cast ushort to float
			Subtract short max
			Divide by short max
			Multiply by the same range encoded with

	- Ball ID's are designed around bitmasks and are as follows:

	byte | Byte 0														| Byte 1														|
	bit  | x80 . x40 . x20 . x10 . x08 . x04 . x02	| x1 .. x80 . x40 . x20 . x10 . x08 . x04 | x02 | x01 |
	ball | 15	 14	 13    12    11    10    9    |  7     6     5     4     3    2     1   |  8  | cue |

 Networking Layout:

   Total size: 78 bytes over network // 39 C# wchar
 
   Address		What						Data type
  
	[ 0x00  ]	ball positions			(compressed quantized vec2's)
	[ 0x40  ]	cue ball velocity		^
	[ 0x44  ]	cue ball angular vel	^
	
	[ 0x48  ]	sn_pocketed				uint16 bitmask ( above table )
	
	[ 0x4A  ]	game state flags		| bit #	| mask	| what				| 
												| 0		| 0x1		| sn_simulating	|
												| 1		| 0x2		| sn_turnid			|
												| 2		| 0x4		| sn_foul			|
												| 3		| 0x8		| sn_open			|
												| 4		| 0x10	| sn_playerxor		|
												| 5		| 0x20	| sn_gameover		|
												| 6		| 0x40	| sn_winnerid		|
												| 7		| 0x80	| sn_permit			|
												
	[ 0x4C  ]	packet #					uint16
	[ 0x4E  ]	gameid					uint16

 Physics Implementation:
	
	Physics are done in 2D to save instructions. The implementation is designed to be
	as numerically stable as possible (eg. using linear algebra as much as possible to
	be explicit about what and where stuff collides ).

	Ball physic response is 100% pure elastic energy transfer, which even at one iteration
	per physics update seems to give plausable enough results. balls can behave like a 
	newtons cradle which is what we want.

	Edge collisions are a little contrived and the reason why the table can ONLY be placed
	at world orign. the table is divided into major and minor sections. some of the 
	calculations can be peeked at here: https://www.geogebra.org/m/jcteyvj6 . It is all
	straight line equations.
	
	There MAY be deviations between SOME client cpus / platforms depending on the floating 
	point architecture, and who knows what the fuck C# will decide to do at runtime anyway. 
	However after some testing this seems rare enough that we could not observe any
	differences at all. If it does happen to be calculated differently, the remote clients
	will catch up with the players game anyway. I reckon this is most likely going to
	affect, if it does at all, only quest/pc crossplay and not much else.

	Physics are calculated on a fixed timestep, using accumulator model. If there is very
	low framerate physics may run at a slower timescale if it passes the threshold where
	maximum updates/frame is reached, but won't affect eventual outcome.
	
	The display balls have their position matched, and rotated based on pure rolling model.
*/

// https://feedback.vrchat.com/feature-requests/p/udon-expose-shaderpropertytoid
// #define USE_INT_UNIFORMS

// Currently unstable..
// #define HT8B_ALLOW_AUTOSWITCH

using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using System;

public class ht8b : UdonSharpBehaviour {

const string FRP_LOW =	"<color=\"#ADADAD\">";
const string FRP_ERR =	"<color=\"#B84139\">";
const string FRP_WARN = "<color=\"#DEC521\">";
const string FRP_YES =	"<color=\"#69D128\">";
const string FRP_END =	"</color>";

[SerializeField] GameObject[]	balls_render;
[SerializeField] public GameObject cuetip;
[SerializeField] GameObject	guideline;
[SerializeField] GameObject	guidefspin;
[SerializeField] GameObject	devhit;
[SerializeField] Text			ltext;

[SerializeField] Vector2		extraGravy;
[SerializeField] GameObject[] playerTotems;
[SerializeField] GameObject[] cueTips;
[SerializeField] Text[]			playerNames;
[SerializeField] Renderer		scoreCardRenderer;
[SerializeField] GameObject	gametable;
[SerializeField] Renderer		tableRenderer;
[SerializeField] GameObject	infBaseTransform;
[SerializeField] Text			infText;
[SerializeField] GameObject	markerObj;
[SerializeField] Renderer		markerRender;
[SerializeField] GameObject	infHowToStart;
[SerializeField] Renderer[]	cueRenderers;

// Audio Components
AudioSource aud_main;

[SerializeField] AudioClip		snd_Intro;
[SerializeField] AudioClip		snd_Sink;
[SerializeField] AudioClip[]	snd_Hits;
[SerializeField] AudioClip		snd_NewTurn;

// REGION GAME STATE
// =========================================================================================================================

[UdonSynced]	private string netstr;		// dumpster fire
					private string netstr_prv;

// Networked game flags
	
uint	sn_pocketed		= 0x00;		// 18 Each bit represents each ball, if it has been pocketed or not
uint sn_pocketed_prv = 0x00;		// -- What was the pocketed balls before we started the simulation

bool	sn_simulating	= false;		// 19:0 (0x01)		True whilst balls are rolling
uint	sn_turnid		= 0x00U;		// 19:1 (0x02)		Whos turn is it, 0 or 1
bool  sn_foul			= false;		// 19:2 (0x04)		End-of-turn foul marker
bool  sn_open			= true;		// 19:3 (0x08)		Is the table open?
uint  sn_playerxor	= 0x00;		// 19:4 (0x10)		What colour the players have chosen
bool  sn_gameover		= true;		// 19:5 (0x20)		Game is complete
uint  sn_winnerid		= 0x00U;		// 19:6 (0x40)		Who won the game if sn_gameover is set

ushort sn_packetid	= 0;			// 20 Current packet number, used for locking updates so we dont accidently go back.
											//    this behaviour was observed on some long connections so its necessary
ushort sn_gameid		= 0;			// 21 Game number

public bool	sn_permit= false;		// Permission for player to play

// Local gamestates

public bool	sn_armed	= false;
bool	sn_updatelock	= false;		// We are waiting for our local simulation to finish, before we unpack data
int	sn_firsthit		= 0;			// The first ball to be hit by cue ball

byte	sn_wins0			= 0;			// Wins for player 0 (unused)
byte	sn_wins1			= 0;			// Wins for player 1 (unused)

float	introAminTimer = 0.0f;		// Ball dropper timer

bool	ballsMoving		= false;		// Tracker variable to see if balls are still on the go

bool	isReposition	= false;			// Repositioner is active
float repoMaxX			= TABLE_WIDTH;	// For clamping to table or set lower for kitchen

// General local aesthetic events
// =========================================================================================================================
	
Color tableSrcColour		= new Color( 1.0f, 1.0f, 1.0f, 1.0f );	// Runtime target colour
Color tableCurrentColour= new Color( 1.0f, 1.0f, 1.0f, 1.0f );	// Runtime actual colour
Color tableColourBlue	= new Color( 0.0f, 0.75f, 1.75f, 1.0f ); // Presets ..
Color tableColourOrange = new Color( 1.75f, 0.25f, 0.0f, 1.0f );
Color tableColourRed		= new Color( 1.2f, 0.0f, 0.0f, 1.0f );
Color tableColorWhite	= new Color( 1.0f, 1.0f, 1.0f, 1.0f );
Color tableColourBlack	= new Color( 0.04f, 0.04f, 0.04f, 1.0f );
Color markerColorOK		= new Color( 0.0f, 1.0f, 0.0f, 1.0f );
Color markerColorNO		= new Color( 1.0f, 0.0f, 0.0f, 1.0f );

// Shader uniforms
#if USE_INT_UNIFORMS

int uniform_tablecolour;
int uniform_scorecard_colour0;
int uniform_scorecard_colour1;
int uniform_scorecard_info;
int uniform_marker_colour;
int uniform_cue_colour;

#else

const string uniform_tablecolour = "_EmissionColour";
const string uniform_scorecard_colour0 = "_Colour0";
const string uniform_scorecard_colour1 = "_Colour1";
const string uniform_scorecard_info = "_Info";
const string uniform_marker_colour = "_Color";
const string uniform_cue_colour = "_ReColor";

#endif

// Updates table colour target to appropriate player colour
void UpdateTableColor( uint idsrc )
{
	if( !sn_open )
	{
		if( (idsrc ^ sn_playerxor) == 0 )
		{
			// Set table colour to blue
			tableSrcColour = tableColourBlue;
		}
		else
		{
			// Table colour to orange
			tableSrcColour = tableColourOrange;
		}

		cueRenderers[ sn_playerxor ].sharedMaterial.SetColor( uniform_cue_colour, tableColourBlue );
		cueRenderers[ sn_playerxor ^ 0x1U ].sharedMaterial.SetColor( uniform_cue_colour, tableColourOrange );
	}
	else
	{
		tableSrcColour = tableColorWhite;

		cueRenderers[ 0 ].sharedMaterial.SetColor( uniform_cue_colour, tableColorWhite );
		cueRenderers[ 1 ].sharedMaterial.SetColor( uniform_cue_colour, tableColourBlack );
	}
}

// Called when a player first sinks a ball whilst the table was previously open
void DisplaySetLocal()
{
	uint picker = sn_turnid ^ sn_playerxor;

	FRP( FRP_YES + "(local) " + Networking.GetOwner( playerTotems[sn_turnid] ).displayName + ":" + sn_turnid + " is " + 
		(picker == 0? "blues": "oranges") + FRP_END );

	UpdateTableColor( sn_turnid );
	UpdateScoreCardLocal();

	scoreCardRenderer.sharedMaterial.SetColor( uniform_scorecard_colour0, sn_playerxor == 0? tableColourBlue: tableColourOrange );
	scoreCardRenderer.sharedMaterial.SetColor( uniform_scorecard_colour1, sn_playerxor == 1? tableColourBlue: tableColourOrange );
}

// End of the game. Both with/loss
void GameOverLocal()
{
	FRP( FRP_YES + "(local) Winner of match: " + Networking.GetOwner( playerTotems[sn_winnerid] ).displayName + FRP_END );

	UpdateTableColor( sn_winnerid );

	infText.text = Networking.GetOwner(playerTotems[sn_winnerid]).displayName + " wins!";
	infBaseTransform.SetActive( true );
	infHowToStart.SetActive( true );

	UpdateScoreCardLocal();
}

void OnTurnChangeLocal()
{
	FRP( FRP_YES + "(local) turn switch to: " + Networking.GetOwner( playerTotems[sn_turnid] ).displayName + FRP_END );

	UpdateTableColor( sn_turnid );

	aud_main.PlayOneShot( snd_NewTurn, 1.0f );

	// Register correct cuetip
	cuetip = cueTips[ sn_turnid ];
}

void UpdateScoreCardLocal()
{
	int[] counter0 = new int[2];

	uint temp = sn_pocketed;

	for( int j = 0; j < 2; j ++ )
	{
		for( int i = 0; i < 7; i ++ )
		{
			if( (temp & 0x4) > 0 )
			{
				counter0[ j ^ sn_playerxor ] ++;
			}

			temp >>= 1;
		}
	}

	// Add black ball if we are winning the thing
	if( sn_gameover )
	{
		counter0[ sn_winnerid ] += (int)((sn_pocketed & 0x2) >> 1);
	}

	scoreCardRenderer.sharedMaterial.SetVector( uniform_scorecard_info, new Vector4( counter0[0]*0.0625f, counter0[1]*0.0625f, 0.0f, 0.0f ) );
}

// Player scored an objective ball
void OnPocketGood()
{
	// Make a bright flash
	tableCurrentColour *= 1.9f;

	aud_main.PlayOneShot( snd_Sink, 1.0f );
}

// Player scored a foul ball (cue, non-objective or 8 before set cleared)
void OnPocketBad()
{
	tableCurrentColour = tableColourRed;

	aud_main.PlayOneShot( snd_Sink, 1.0f );
}

void ShowBalls( bool state )
{
	for( int i = 0; i < 16; i ++ )
	{
		balls_render[ i ].SetActive( state );
	}
}

void NewGameLocal()
{
	VRCPlayerApi startPlayer = Networking.GetOwner(playerTotems[0]);
	FRP( FRP_YES + "(local) " + ( startPlayer != null? startPlayer.displayName: "[null]" ) + " started a new game" + FRP_END );

	// Put names on the board
	if( startPlayer != null )
	{
		playerNames[0].text = Networking.GetOwner(playerTotems[0]).displayName;
		playerNames[1].text = Networking.GetOwner(playerTotems[1]).displayName;
	}

	//tableSrcColour = tableColorWhite;
	UpdateTableColor( 0 );

	introAminTimer = 2.0f;
	aud_main.PlayOneShot( snd_Intro, 1.0f );

	// Turn off info
	infBaseTransform.SetActive( false );
	infHowToStart.SetActive( false );

	ShowBalls( true );

	UpdateScoreCardLocal();

	isReposition = false;
}

// REGION PHYSICS ENGINE
// =========================================================================================================================

// Cue input tracking

Vector3	cue_lpos;
Vector3	cue_llpos;
Vector3	cue_vdir;
Vector2	cue_shotdir;
float		cue_fdir;

// Timing

const float MAX_DELTA = 0.1f;						// Maximum steps/frame ( 8 )
const float FIXED_TIME_STEP = 0.0125f;			// time step in seconds per iteration
const float TIME_ALPHA = 50.0f;					// (unused) physics interpolation

// Calculation constants (measurements are in meters)

const float TABLE_WIDTH		= 1.0668f;					// horizontal span of table
const float TABLE_HEIGHT	= 0.6096f;					// vertical span of table
const float BALL_DIAMETRE	= 0.06f;						// width of ball
const float BALL_1OR			= 16.66666666666666f;	// 1 over ball radius
const float BALL_RSQR		= 0.0009f;					// ball radius squared
const float BALL_DSQR		= 0.0036f;					// ball diameter squared
const float POCKET_RADIUS	= 0.09f;						// Full diameter of pockets (exc ball radi)

const float K_1OR2			= 0.70710678118f;			// 1 over root 2 (normalize +-1,+-1 vector)
const float K_1OR5			= 0.4472135955f;			// 1 over root 5 (normalize +-1,+-2 vector)

const float POCKET_DEPTH	= 0.04f;						// How far back (roughly) do pockets absorb balls after this point
const float MIN_VELOCITY	= 0.00005625f;				// SQUARED

const float FRICTION_EFF	= 0.99f;						// How much to multiply velocity by each update

// Physics memory

Vector2[] ball_co = new Vector2[16];	// Current positions
Vector2[] ball_og = new Vector2[16];	// Break positions
Vector2[] ball_vl = new Vector2[16];	// Current velocities
Vector2	 cue_avl = Vector2.zero;		// Cue ball angular velocity

// Send ball to the gulag
void PocketBall( int id )
{
	uint total = 0U;

	// Get total for X positioning
	for( int i = 0; i < 16; i ++ )
	{
		total += (sn_pocketed >> i) & 0x1U;
	}

	// Put balls on the edge of the table for now
	// TODO: propper display
	ball_co[ id ].x = -TABLE_WIDTH + (float)total * BALL_DIAMETRE;
	ball_co[ id ].y = TABLE_HEIGHT + BALL_DIAMETRE * 2.0f;

	sn_pocketed ^= 1U << id;

	uint bmask = 0x1FCU << ((int)(sn_turnid ^ sn_playerxor) * 7);

	// Good pocket
	if( ((0x1U << id) & ((bmask) | (sn_open ? 0xFFFCU: 0x0000U) | ((bmask & sn_pocketed) == bmask? 0x2U: 0x0U))) > 0 )
	{
		OnPocketGood();
	}
	else
	{
		// bad
		OnPocketBad();
	}
}

// TODO: Inline
bool BallInPlay( int id )
{
	return ((sn_pocketed >> id) & 0x1U) == 0x00U;
}

// Check pocket condition
void BallPockets( int id )
{
	if( !BallInPlay( id ) )
		return;

	float zy, zx;
	Vector2 A;

	A = ball_co[ id ];

	// Setup major regions
	zx = A.x > 0.0f ? 1.0f: -1.0f;
	zy = A.y > 0.0f ? 1.0f: -1.0f;

	// Its in a pocket
	if( A.y*zy > TABLE_HEIGHT + POCKET_DEPTH || A.y*zy > A.x*-zx + TABLE_WIDTH+TABLE_HEIGHT + POCKET_DEPTH )
	{
		PocketBall( id );
	}
}

// Makes sure that velocity is not opposing surface normal
void ClampBallVelSemi( int id, Vector2 surface )
{
	// TODO: improve this method to be a bit more accurate
	if( Vector2.Dot( ball_vl[id], surface ) < 0.0f )
	{
		ball_vl[id] = ball_vl[id].magnitude * surface;
	}
}

// Is cue touching another ball?
bool CueContacting()
{
	for( int i = 1; i < 16; i ++ )
	{
		if( (ball_co[0] - ball_co[i]).sqrMagnitude < BALL_DSQR )
		{
			return true;
		}
	}

	return false;
}

// TODO: inline this
void BallEdges( int id )
{
	if( !BallInPlay( id ) )
		return;

	float zy, zx, zz, zw, d, k, i, j, l, r;
	Vector2 A, N;

	A = ball_co[ id ];

	// REGIONS
	/*  
		*  QUADS:							SUBSECTION:				SUBSECTION:
		*    zx, zy:							zz:						zw:
		*																
		*  o----o----o  +:  1			\_________/				\_________/
		*  | -+ | ++ |  -: -1		     |	    /		              /  /
		*  |----+----|					  -  |  +   |		      -     /   |
		*  | -- | +- |						  |	   |		          /  +  |
		*  o----o----o						  |      |             /       |
		* 
		*/

	// Setup major regions
	zx = A.x > 0.0f ? 1.0f: -1.0f;
	zy = A.y > 0.0f ? 1.0f: -1.0f;

	// within pocket regions
	if( (A.y*zy > (TABLE_HEIGHT-POCKET_RADIUS)) && (A.x*zx > (TABLE_WIDTH-POCKET_RADIUS) || A.x*zx < POCKET_RADIUS) )
	{
		// Subregions
		zw = A.y * zy > A.x * zx - TABLE_WIDTH + TABLE_HEIGHT ? 1.0f : -1.0f;

		// Normalization / line coefficients change depending on sub-region
		if( A.x * zx > TABLE_WIDTH * 0.5f )
		{
			zz = 1.0f;
			r = K_1OR2;
		}
		else
		{
			zz = -2.0f;
			r = K_1OR5;
		}

		// Collider line EQ
		d = zx * zy * zz; // Coefficient
		k = (-(TABLE_WIDTH * Mathf.Max(zz, 0.0f)) + POCKET_RADIUS * zw * Mathf.Abs( zz ) + TABLE_HEIGHT) * zy; // Konstant

		// Check if colliding
		l = zw * zy;
		if( A.y * l > (A.x * d + k) * l )
		{
			// Get line normal
			N = new Vector2(zx * zz, -zy) * zw * r;

			// New position
			i = (A.x * d + A.y - k) / (2.0f * d);
			j = i * d + k;

			ball_co[ id ] = new Vector2( i, j );

			// Reflect velocity
			ball_vl[ id ] = Vector2.Reflect( ball_vl[ id ], N );

			ClampBallVelSemi( id, N );
		}
	}
	else // edges
	{
		if( A.x * zx > TABLE_WIDTH )
		{
			ball_co[id].x = TABLE_WIDTH * zx;
			ball_vl[id] = Vector2.Reflect( ball_vl[id], Vector2.left * zx );

			ClampBallVelSemi( id, Vector2.left * zx );
		}

		if( A.y * zy > TABLE_HEIGHT )
		{
			ball_co[id].y = TABLE_HEIGHT * zy;
			ball_vl[id] = Vector2.Reflect( ball_vl[id], Vector2.down * zy );

			ClampBallVelSemi( id, Vector2.down * zy );
		}
	}
}

// Advance simulation 1 step for ball id
void BallSimulate( int id )
{
	if( !BallInPlay( id ) )
		return;

	// Apply friction
	ball_vl[ id ] *= FRICTION_EFF;

	Vector2 mov_delta = ball_vl[id] * FIXED_TIME_STEP;
	float mov_mag = mov_delta.magnitude;

	// Apply movement
	ball_co[ id ] += mov_delta;

	// Rotate visual object by pure rolling
	balls_render[ id ].transform.Rotate( new Vector3( mov_delta.y, 0.0f, -mov_delta.x ) / mov_mag, mov_mag * BALL_1OR * Mathf.Rad2Deg, Space.World );

	// ball/ball collisions
	for( int i = id+1; i < 16; i++ )
	{
		if( !BallInPlay( id ) )
			continue;

		Vector2 delta = ball_co[ i ] - ball_co[ id ];
		float dist = delta.magnitude;

		if( dist < BALL_DIAMETRE )
		{
			Vector2 normal = delta / dist;

			Vector2 velocityDelta = ball_vl[ id ] - ball_vl[ i ];

			float dot = Vector2.Dot( velocityDelta, normal );

			if( dot > 0.0f ) 
			{
				Vector2 reflection = normal * dot;
				ball_vl[id] -= reflection;
				ball_vl[i] += reflection;

				//aud_click.volume = Mathf.Clamp( ball_velocities[id].sqrMagnitude * 0.2f, 0.0f, 1.0f ); 
					
				// Prevent sound spam if it happens
				if( ball_vl[id].sqrMagnitude > 0 )
					aud_main.PlayOneShot( snd_Hits[ 0 ], 1.0f );

				// First hit detected
				if( id == 0 && sn_firsthit == 0 )
				{
					sn_firsthit = i;
				}
			}
		}
	}

	// ball still moving about
	if( ball_vl[ id ].sqrMagnitude > MIN_VELOCITY )
	{
		ballsMoving = true;
	}
	else
	{
		// Put velocity to 0
		ball_vl[ id ] = Vector2.zero;
	}
}

// Ray circle intersection
// yes, its fixed size circle
// Output is dispensed into the below variable
// One intersection point only
// This is not used in physics calcuations, only cue input

Vector2 RayCircle_output;
bool RayCircle( Vector2 start, Vector2 dir, Vector2 circle )
{
	Vector2 nrm = dir.normalized;
	Vector2 h = circle - start;
	float lf = Vector2.Dot( nrm, h );
	float s = BALL_RSQR - Vector2.Dot( h, h ) + lf * lf;

	if( s < 0.0f ) return false;

	s = Mathf.Sqrt( s );

	if( lf < s )
	{
		if( lf + s >= 0 )
		{
			s = -s;
		}
		else
		{
			return false;
		}
	}

	RayCircle_output = start + nrm * (lf-s);
	return true;
}

Vector3 RaySphere_output;
bool RaySphere( Vector3 start, Vector3 dir, Vector3 sphere )
{
	Vector3 nrm = dir.normalized;
	Vector3 h = sphere - start;
	float lf = Vector3.Dot( nrm, h );
	float s = BALL_RSQR - Vector3.Dot(h, h) + lf * lf;

	if( s < 0.0f ) return false;

	s = Mathf.Sqrt( s );

	if( lf < s )
	{
		if( lf + s >= 0 )
		{
			s = -s;
		}
		else
		{
			return false;
		}
	}

	RaySphere_output = start + nrm * (lf-s);
	return true;
}

// Closest point on line from pos
Vector2 LineProject( Vector2 start, Vector2 dir, Vector2 pos )
{
	return start + dir * Vector2.Dot( pos - start, dir );
}

// Setup player's turn
void Owner_NewTurn()
{
	FRP( FRP_YES + "NewTurn()" + FRP_END );

	// Fixup game state
	if( sn_foul )
	{
		FRP( FRP_LOW + "Game state fixup" + FRP_END );

		// Allow repositioning anywhere
		isReposition = true;
		repoMaxX = TABLE_WIDTH;
		markerObj.SetActive( true );

		// Cue ball is out of play
		if( (sn_pocketed & 0x1U) != 0 )
		{
			ball_co[0] = ball_og[0];
			ball_vl[0] = Vector2.zero;
			
			markerObj.transform.position = Vector3.zero;

			// Save out position
			// NetPack( sn_turnid );

			// https://vrchat.canny.io/vrchat-udon-closed-alpha-feedback/p/bitwisenot-for-integer-built-in-types
			// sn_pocketed &= ~0x1U;

			sn_pocketed &= 0xFFFFFFFEU;
		}
		else
		{
			markerObj.transform.position = new Vector3( ball_co[0].x, 0.0f, ball_co[0].y );
		}
	}

	sn_permit = true;
	sn_foul = false;
	sn_firsthit = 0;

	// Propogate any updates we made
	NetPack( sn_turnid );
}

// once balls stops rolling this is called
void SimEnd()
{
	sn_simulating = false;

	FRP( FRP_LOW + "(local) SimEnd()" + FRP_END );

	// TODO: split state checking into more manageable chunks
	if( Networking.GetOwner( this.gameObject ) == Networking.LocalPlayer )
	{
		// Owner state checks
		FRP( FRP_LOW + "Post-move state checking" + FRP_END );

		// We might need this later
		uint bmask = 0x1FCU << ((int)(sn_playerxor ^ sn_turnid) * 7);

		// Check for fouls
		if( (sn_pocketed & 0x1U) == 0x1U )
		{
			FRP( FRP_ERR + "FOUL: scratched" + FRP_END );
			sn_foul = true;

			// TODO: remove code dupe
			if(((sn_pocketed & bmask) != bmask && (sn_pocketed & 0x2U) == 0x2U))
			{
				FRP( FRP_ERR + "LOSS: sunk white and black" + FRP_END );

				sn_gameover = true;
				sn_winnerid = sn_turnid ^ 0x1U;

				GameOverLocal();

				NetPack( sn_turnid );
				NetRead();

				return;
			}
		}
		else if( (sn_pocketed & bmask) != bmask && (sn_pocketed & 0x2U) == 0x2U )
		{
			FRP( FRP_ERR + "LOSS: potted 8 ball before completing set" + FRP_END );

			sn_gameover = true;
			sn_winnerid = sn_turnid ^ 0x1U;

			GameOverLocal();

			NetPack( sn_turnid );
			NetRead();

			return;
		}
		else
		{
			// Check first hit rules
			// No hit
			if ( sn_firsthit == 0 )
			{
				FRP( FRP_ERR + "FOUL: cue diddn't hit anything" + FRP_END );
				sn_foul = true;
			}
			else
			{
				// Check for non-objective
				if( !sn_open )
				{
					// TODO: this can probably be reduced down a bit in terms of the amount of
					// conditional logic here and baked into bitmasks.
					
					uint çhkmask = bmask;

					// Append black ball if all set is complete
					if( (bmask & sn_pocketed) == bmask )
					{
						çhkmask |= 0x2U;
					}

					// Check first hit within mask
					if( ((0x1 << sn_firsthit) & çhkmask) == 0 )
					{
						FRP( FRP_ERR + "FOUL: cue hit non objective ball" + FRP_END );
						sn_foul = true;
					}
				}
			}
		}

		if( sn_foul )
		{
			// Flip player bit and commit, reciever will take ownership once update propogates
			FRP( FRP_LOW + "Transferring ownership" + FRP_END );

			NetPack( sn_turnid ^ 0x1U );
			NetRead();
		}
		else
		{
			FRP( FRP_YES + "Legal move confirmed" + FRP_END );

			bool oppturn = false;

			// Check if we pocketed a ball that is our type
			if( sn_open )
			{
				// Every ball in game in mainplay is valid
				if((sn_pocketed & 0xFFFC) > (sn_pocketed_prv & 0xFFFC))
				{
					// Player triggered turn xor
					// check which group has the most sinks and 
					if((sn_pocketed & 0x1FC) > ((sn_pocketed & 0xFE00) >> 7))
					{
						sn_playerxor = sn_turnid;
						// FRP( FRP_YES + "(local) Player is oranges!" + FRP_END );
					}
					else
					{
						sn_playerxor = sn_turnid ^ 0x1u;
						// FRP( FRP_YES + "(local) Player is blues!" + FRP_END );
					}

					sn_open = false;

					DisplaySetLocal();

					// Update needs to be sent about playerxor etc
					NetPack( sn_turnid );
					NetRead();
				}
				else
				{
					oppturn = true;
				}
			}
			else
			{
				// Check we sunk at least one correct ball
				if((sn_pocketed & bmask) > (sn_pocketed_prv & bmask))
				{
					FRP( FRP_YES + "Objective ball sunk, continuing" + FRP_END );
				}
				else
				{
					if((sn_pocketed & bmask) == bmask && (sn_pocketed & 0x2U) == 0x2U)
					{
						FRP( FRP_YES + "(local) Player wins!" + FRP_END );

						sn_gameover = true;
						sn_winnerid = sn_turnid;

						GameOverLocal();

						NetPack( sn_turnid );
						NetRead();
							
						return;
					}
					else
					{
						FRP( FRP_LOW + "No objective ball made" + FRP_END );
						oppturn = true;
					}
				}
			}

			if( oppturn )
			{
				FRP( FRP_LOW + "Turn will not be extended, transferring ownership" + FRP_END );

				NetPack(sn_turnid ^ 0x1U);
				NetRead();
			}
			else
			{
				// Everything was fine, player can go againf
				Owner_NewTurn();
			}
		}
	}
	else
	{
		// Check if there was a network update on hold
		if( sn_updatelock )
		{
			FRP( FRP_LOW + "Update was waiting, executing now" + FRP_END );
			sn_updatelock = false;

			NetRead();
		}
	}

	UpdateScoreCardLocal();
}

// Run one physics iteration for all balls
void PhysicsUpdate()
{
	ballsMoving = false;

	// Cue angular velocity
	if( (sn_pocketed & 0x1) == 0 )
	{
		cue_avl *= 0.96f;
		ball_vl[0] += cue_avl * FIXED_TIME_STEP;
	}

	// Run main simulation / inter-ball collision
	for( int i = 0; i < 16; i ++ )
	{
		BallSimulate( i );
	}

	// Check if simulation has settled
	if( !ballsMoving )
	{
		if( sn_simulating )
		{
			SimEnd();
		}

		return;
	}

	// Run edge collision
	for( int i = 0; i < 16; i ++ )
	{
		BallEdges( i );
	}

	// Run triggers
	for( int i = 0; i < 16; i ++ )
	{
		BallPockets( i );
	}
}

// Events
public void StartHit()
{
	// lock aim variables
	sn_armed = true;
}

public void EndHit()
{
	sn_armed = false;
}

public void PosFinalize()
{
	if( !CueContacting() )
	{
		isReposition = false;
		markerObj.SetActive( false );

		// Save out position to remote clients
		NetPack( sn_turnid );
	}
}

float timeLast;
float accum;

private void Update()
{
	// Physics step accumulator routine
	float time = Time.timeSinceLevelLoad;
	float timeDelta = time - timeLast;

	if ( timeDelta > MAX_DELTA )
	{
		timeDelta = MAX_DELTA;
	}

	timeLast = time;
		
	// Run sim only if things are moving
	if( sn_simulating )
	{
		accum += timeDelta;

		while ( accum >= FIXED_TIME_STEP )
		{
			PhysicsUpdate();
			accum -= FIXED_TIME_STEP;
		}
	}

	// Update rendering objects positions
	for( int i = 0; i < 16; i ++ )
	{
		balls_render[i].transform.position = new Vector3( ball_co[i].x, 0.0f, ball_co[i].y );
	}

	cue_lpos = cuetip.transform.position;
	Vector3 lpos2 = cue_lpos;

	// cue ball in world space
	Vector3 ball0ws = new Vector3(ball_co[0].x, 0.0f, ball_co[0].y);
	
	// if shot is prepared for next hit
	if( sn_permit )
	{
		bool isContact = false;

		if( isReposition )
		{
			// Clamp position to table / kitchen
			Vector3 temp = markerObj.transform.position;
			temp.x = Mathf.Clamp( temp.x, -TABLE_WIDTH, repoMaxX );
			temp.z = Mathf.Clamp( temp.z, -TABLE_HEIGHT, TABLE_HEIGHT );
			temp.y = 0.0f;
			markerObj.transform.position = temp;
			markerObj.transform.rotation = Quaternion.identity;

			ball_co[0] = new Vector2( temp.x, temp.z );
			balls_render[0].transform.position = temp;

			isContact = CueContacting();

			if( isContact )
			{
				markerRender.sharedMaterial.SetColor( uniform_marker_colour, markerColorNO );
			}
			else
			{
				markerRender.sharedMaterial.SetColor( uniform_marker_colour, markerColorOK );
			}
		}

		if( sn_armed && !isContact )
		{
			float sweep_time_ball = Vector3.Dot( ball0ws - cue_llpos, cue_vdir );

			// Check for potential skips due to low frame rate
			if( sweep_time_ball > 0.0f && sweep_time_ball < (cue_llpos - lpos2).magnitude )
			{
				lpos2 = cue_llpos + cue_vdir * sweep_time_ball;
			}

			// Hit condition is when cuetip is gone inside ball
			if( (lpos2 - ball0ws).sqrMagnitude < BALL_RSQR )
			{
				

#if HT8B_ALLOW_AUTOSWITCH
				// This check is here for stability when using auto-transfer
				if( Networking.GetOwner( playerTotems[ sn_turnid ] ) == Networking.LocalPlayer )
#else
				if( Networking.GetOwner( this.gameObject ) == Networking.LocalPlayer )
#endif
				{
					// Make sure repositioner is turned off if the player decides he just
					// wanted to hit it without putting it somewhere
					isReposition = false;
					markerObj.SetActive( false );

					devhit.SetActive( false );
					guideline.SetActive( false );

					// Compute velocity delta
					float vel = (lpos2 - cue_llpos).magnitude * 10.0f;

					// weeeeeeee
					ball_vl[0] = cue_shotdir * Mathf.Min( vel, 1.0f ) * 14.0f;

					// ball avl is a function of velocity
					cue_avl = ball_vl[0] * RaySphere_output.y * 33.3333333333f;

					// Remove locks
					sn_armed = false;
					sn_permit = false;

					FRP( FRP_LOW + "Commiting changes" + FRP_END );

					// Commit changes
					sn_simulating = true;
					sn_pocketed_prv = sn_pocketed;

					NetPack( sn_turnid );
					NetRead();
				}
			}
		}
		else
		{
			cue_vdir = cuetip.transform.forward;//new Vector2( cuetip.transform.forward.z, -cuetip.transform.forward.x ).normalized;

			// Get where the cue will strike the ball
			if( RaySphere( lpos2, cue_vdir, ball0ws ))
			{
				guideline.SetActive( true );
				devhit.SetActive( true );
				devhit.transform.position = RaySphere_output;
				guidefspin.transform.localScale = new Vector3( RaySphere_output.y * 33.3333333333f, 1.0f, 1.0f );

				Vector3 scuffdir = ( ball0ws - RaySphere_output ).normalized * 0.5f;
				cue_shotdir = new Vector2( cue_vdir.x, cue_vdir.z );

				cue_shotdir += new Vector2( scuffdir.x, scuffdir.z );
				cue_shotdir = cue_shotdir.normalized;

				// TODO: add scuff offset to vdir
				cue_fdir = Mathf.Atan2( cue_shotdir.y, cue_shotdir.x );

				// Update the prediction line direction
				guideline.transform.eulerAngles = new Vector3( 0.0f, -cue_fdir * Mathf.Rad2Deg, 0.0f );
			}
			else
			{
				devhit.SetActive( false );
				guideline.SetActive( false );
			}
		}
	}

	cue_llpos = lpos2;

	// Table outline colour
	if( sn_gameover )
	{
		// Flashing if we won
		tableCurrentColour = tableSrcColour * (Mathf.Sin( Time.timeSinceLevelLoad * 3.0f) * 0.5f + 1.0f);
		
		infBaseTransform.transform.position = new Vector3( 0.0f, Mathf.Sin( Time.timeSinceLevelLoad ) * 0.1f, 0.0f );
		infBaseTransform.transform.Rotate( Vector3.up, 90.0f * Time.deltaTime );
	}
	else
	{
		tableCurrentColour = Color.Lerp( tableCurrentColour, tableSrcColour, Time.deltaTime * 3.0f );
	}

	tableRenderer.sharedMaterial.SetColor( uniform_tablecolour, tableCurrentColour );

	// Intro animation
	if( introAminTimer > 0.0f )
	{
		introAminTimer -= Time.deltaTime;

		Vector3 temp;
		float atime;
		float aitime;

		if( introAminTimer < 0.0f )
			introAminTimer = 0.0f;

		// Cueball drops late
		temp = balls_render[0].transform.position;
		atime = Mathf.Clamp(introAminTimer - 0.33f, 0.0f, 1.0f); 
		aitime = (1.0f - atime);
		temp.y = Mathf.Abs(Mathf.Cos(atime * 6.29f)) * atime * 0.5f;
		balls_render[0].transform.position = temp;
		balls_render[0].transform.localScale = new Vector3(aitime, aitime, aitime);

		for ( int i = 1; i < 16; i ++ )
		{
			temp = balls_render[i].transform.position;
			atime = Mathf.Clamp(introAminTimer - 0.84f - (float)i * 0.03f, 0.0f, 1.0f);
			aitime = (1.0f - atime);

			temp.y = Mathf.Abs( Mathf.Cos( atime * 6.29f ) ) * atime * 0.5f;
			balls_render[i].transform.position = temp;
			balls_render[i].transform.localScale = new Vector3(aitime, aitime, aitime);
		}
	}
}

private void Start()
{
	FRP( FRP_LOW + "Starting" + FRP_END );

#if USE_INT_UNIFORMS

	// Gather shader uniforms
	uniform_tablecolour = Shader.PropertyToID( "_EmissionColour" );
	uniform_scorecard_colour0 = Shader.PropertyToID( "_Colour0" );
	uniform_scorecard_colour1 = Shader.PropertyToID( "_Colour1" );
	uniform_scorecard_info = Shader.PropertyToID( "_Info" );
	uniform_marker_colour = Shader.PropertyToID( "_Color" );
	uniform_cue_colour = Shader.PropertyToID( "_ReColor" );
	
#endif

	UpdateTableColor( 0 );

	aud_main = this.GetComponent<AudioSource>();
	//tableRenderer = gametable.GetComponent<Renderer>();

	// turn off guideline
	guideline.SetActive( false );
	devhit.SetActive( false );
	infBaseTransform.SetActive( false );
	markerObj.SetActive( false );

	for( int i = 0; i < 16; i ++ ) 
	{
		ball_og[i].x = balls_render[i].transform.position.x;
		ball_og[i].y = balls_render[i].transform.position.z;
		balls_render[i].SetActive(false);
	}

	//SetupBreak();

	NetPack( 0 );
	NetRead();
}

// Resets local game state to defined state
// TODO: Merge this with NewGame()
public void SetupBreak()
{
	FRP( FRP_LOW + "SetupBreak()" + FRP_END );

	sn_pocketed = 0x00;
	sn_pocketed_prv = 0x00;
	sn_simulating = false;
	sn_open = true;
	sn_gameover = false;

	// Doesnt need to be set but for consistencys sake
	sn_playerxor = 0;
	sn_winnerid = 0;

	for( int i = 0; i < 16; i ++ )
	{
		ball_co[ i ] = ball_og[ i ];
		ball_vl[ i ] = Vector2.zero;
	}

	NewGameLocal();
}

public void SendDebugImpulse()
{
	FRP( "Resetting" );

	SetupBreak();

	// Re-encode positions
	NetPack( 0 );
	NetRead();
}

// ** experimental ** yoink turn from other player
// TODO: maybe review transfer system to instead only use
// cue IDs.

public void AutoTake0()
{
	if( sn_turnid == 0 && sn_permit )
	{
		Networking.SetOwner( Networking.LocalPlayer, this.gameObject );
	}
}

public void AutoTake1()
{
	if( sn_turnid == 1 && sn_permit )
	{
		Networking.SetOwner( Networking.LocalPlayer, this.gameObject );
	}
}

public void NewGame()
{
	// This will get called by all clients who observe the collision
	// between the two sticks. Therefore extra checks are done to make
	// sure this only runs predictably

	FRP( FRP_LOW + "(local) NewGame()" + FRP_END );

	if( Networking.GetOwner( playerTotems[0] ) == Networking.LocalPlayer )
	{
		// Check if game in progress
		if( sn_gameover )
		{
			FRP( FRP_YES + "Starting new game" + FRP_END );
			
			Networking.SetOwner( Networking.LocalPlayer, this.gameObject );

			sn_gameid ++;

			SetupBreak();

			// Override allow repositioning within kitchen
			isReposition = true;
			repoMaxX = -TABLE_WIDTH * 0.5f;
			markerObj.transform.position = new Vector3( ball_og[0].x, 0.0f, ball_og[0].y );
			markerObj.SetActive( true );

			Owner_NewTurn();

			// TODO: send which totem ID started the game instead
			NetPack( 0 );
			NetRead();
		}
		else
		{
			FRP( FRP_WARN + "game in progress" + FRP_END );
		}
	}
	else
	{
		// FRP( FRP_WARN + "(local) not player 0" + FRP_END );
	}
}

// reset game
public void ForceEndGame()
{
	// Limit reset to totem owners ownly
	if( Networking.LocalPlayer == Networking.GetOwner( playerTotems[0] ) ||
		Networking.LocalPlayer == Networking.GetOwner( playerTotems[1] ))
	{
		FRP( FRP_WARN + "Ending game early" + FRP_END );

		Networking.SetOwner( Networking.LocalPlayer, this.gameObject );

		sn_gameover = true;
		sn_simulating = false;
		sn_permit = false;

		// sn_winnerid		= 0x00U;

		// For good measure in case other clients trigger an event whilst owner
		sn_packetid += 2;

		GameOverLocal();

		NetPack( sn_turnid );
	}
}

// REGION NETWORKING
// =========================================================================================================================

const float I16_MAXf = 32767.0f;

// 2 char string from unsigned short
string EncodeUint16( ushort sh )
{
	return "" + (char)sh;
}

// 4 char string from Vector2. Encodes floats in: [ -range, range ] to 0-65535
string Encodev2( Vector2 vec, float range )
{
	ushort x = (ushort)((vec.x / range) * I16_MAXf + I16_MAXf );
	ushort y = (ushort)((vec.y / range) * I16_MAXf + I16_MAXf );

	return EncodeUint16(x) + EncodeUint16(y);
}

// 2 chars at index to ushort
ushort DecodeUint16( char[] arr, int start )
{
	return (ushort)arr[start];
}

// Decode 4 chars at index to Vector2. Decodes from 0-65535 to [ -range, range ]
Vector2 Decodev2( char[] arr, int start, float range )
{
	float x = (((float)DecodeUint16(arr, start) - I16_MAXf) / I16_MAXf) * range;
	float y = (((float)DecodeUint16(arr, start + 1) - I16_MAXf) / I16_MAXf) * range;
		
	return new Vector2(x,y);
} 
	 
// Encode all data of game state into netstr
public void NetPack( uint _turnid )
{
	string enc = "";
	sn_packetid ++;

	// positions
	for ( int i = 0; i < 16; i ++ )
	{
		string coded = Encodev2(ball_co[i], 2.5f);
		enc += coded;
	}

	// Cue ball velocity last
	enc += Encodev2( ball_vl[0], 50.0f );
	enc += Encodev2( cue_avl, 50.0f );

	// Encode pocketed imformation
	enc += EncodeUint16( (ushort)(sn_pocketed & 0x0000FFFFU) );

	// Game state
	uint flags = 0x0U;
	if( sn_simulating ) flags |= 0x1U;
	flags |= _turnid << 1;
	if( sn_foul ) flags |= 0x4U;
	if( sn_open ) flags |= 0x8U;
	flags |= sn_playerxor << 4;
	if( sn_gameover ) flags |= 0x20U;
	flags |= sn_winnerid << 6;
	if( sn_permit ) flags |= 0x80U;

	enc += EncodeUint16( (ushort)flags );
	enc += EncodeUint16( sn_packetid );
	enc += EncodeUint16( sn_gameid );

	netstr = enc;

	FRP( FRP_LOW + "NetPack()" + FRP_END );
}

// Decode networking string
// TODO: Clean up this function
public void NetRead()
{
	FRP( FRP_LOW + netstr_hex() + FRP_END );

	if( netstr.Length < 39 )
	{
		FRP( FRP_WARN + "Sync string too short for decode, skipping\n" + FRP_END );
		return;
	}

	char[] arr = netstr.ToCharArray();
		
	// Throw out updates that are possible errournous
	ushort nextid = DecodeUint16( arr, 0x26 );
	if( nextid < sn_packetid )
	{
		FRP( FRP_WARN + "Packet ID was old ( " + nextid + " < " + sn_packetid + " ). Throwing out update" + FRP_END );
		return;
	}
	sn_packetid = nextid;

	// Pocketed information
	sn_pocketed = DecodeUint16( arr, 0x24 );

	// Check for new game
	ushort nextgame = DecodeUint16( arr, 0x27 );
	if( nextgame > sn_gameid )
	{
		NewGameLocal();
	}
	sn_gameid = nextgame;

	for( int i = 0; i < 16; i ++ )
	{
		ball_vl[i] = Vector2.zero;
		ball_co[i] = Decodev2( arr, i * 2, 2.5f );
	}

	ball_vl[0] = Decodev2( arr, 0x20, 50.0f );
	cue_avl = Decodev2( arr, 0x22, 50.0f );

	// Game state
	uint gamestate = DecodeUint16( arr, 0x25 );
	sn_simulating = (gamestate & 0x1U) == 0x1U;
	sn_foul = (gamestate & 0x4U) == 0x4U;
	sn_playerxor = (gamestate & 0x10U) >> 4;
	sn_winnerid = (gamestate & 0x40U) >> 6;
	sn_permit = (gamestate & 0x80U) == 0x80U;

	if( !sn_permit )
	{
		markerObj.SetActive( false );
		devhit.SetActive( false );
		guideline.SetActive( false );
	}

	bool openlast = sn_open; 
	sn_open = (gamestate & 0x8U) == 0x8U;

	// Check if turn was transferred
	uint newturn = (gamestate & 0x2U) >> 1;
	if( sn_turnid != newturn )
	{
		FRP( FRP_LOW + "Ownership changed" + FRP_END );

		sn_turnid = newturn;

		// Fullfil ownership transfer early
		// Technically this is not needed with auto-switch mechanism, however its currently
		// not implemented anywhere else when a turn switch is made and both players are
		// already holding the respective cues, its not gonna let a player play cause
		// he doesnt have ownership of the script object
		//
		// TODO: Polish the auto-yoink system.

		if( Networking.GetOwner( playerTotems[ sn_turnid ] ) == Networking.LocalPlayer )
		{
			FRP( FRP_YES + "Transfered to local" + FRP_END );

			if( sn_simulating )
			{
				// In THEORY this should never ever be hit, but there might be an edge case
				FRP( FRP_ERR + "Remote simulating when ownership transfer attempt was made... script is deadlocked! contact harry!" + FRP_END );
			}
			else
			{
				// Give our local player permission to play his turn
				Networking.SetOwner( Networking.LocalPlayer, this.gameObject );
					
				// Sort out gamestate
				Owner_NewTurn();
					
				// Not sure why these were called ?
				// NetPack( sn_turnid );
				// NetRead();
			}
		}
		else
		{
			FRP( FRP_LOW + "Transfered to remote" + FRP_END );
		}

		OnTurnChangeLocal();
	}

	if(openlast && !sn_open)
	{
		DisplaySetLocal();
	}

	// Check if game is over
	bool gameover = (gamestate & 0x20U) == 0x20U;
	if (gameover && !sn_gameover)
	{
		GameOverLocal();
	}
	sn_gameover = gameover;
}

string netstr_hex()
{
	char[] arr = netstr.ToCharArray();
	string str = "";

	for( int i = 0; i < netstr.Length; i ++ )
	{
		ushort v = DecodeUint16( arr, i );
		str += v.ToString("X4");
	}

	return str;
}

// Wait for updates to the synced netstr
public override void OnDeserialization()
{
	if( !string.Equals( netstr, netstr_prv ) )
	{
		FRP( FRP_LOW + "OnDeserialization() :: netstr update" + FRP_END );

		netstr_prv = netstr;

		// Check if local simulation is in progress, the event will fire off later when physics
		// are settled by the client
		if( sn_simulating )
		{
			FRP( FRP_WARN + "local simulation is still running, the network update will occur after completion" + FRP_END );
			sn_updatelock = true;
		}
		else
		{
			// We are free to read this update
			NetRead();
		}
	}
}

const int FRP_MAX = 32;
int FRP_LEN = 0;
int FRP_PTR = 0;
string[] FRP_LINES = new string[32];

// Print a line to the debugger
void FRP( string ln )
{
	Debug.Log( "[<color=\"#B5438F\">ht8b</color>] " + ln );

	FRP_LINES[ FRP_PTR ++ ] = "[<color=\"#B5438F\">ht8b</color>] " + ln + "\n";
	FRP_LEN ++ ;

	if( FRP_PTR >= FRP_MAX )
	{
		FRP_PTR = 0;
	}

	if( FRP_LEN > FRP_MAX )
	{
		FRP_LEN = FRP_MAX;
	}

	string output = "ht8b 0.1.2a ";
		
	// Add information about game state:
	output += Networking.IsOwner(Networking.LocalPlayer, this.gameObject) ? 
		"<color=\"#95a2b8\">net(</color> <color=\"#4287F5\">OWNER</color> <color=\"#95a2b8\">)</color> ":
		"<color=\"#95a2b8\">net(</color> <color=\"#678AC2\">RECVR</color> <color=\"#95a2b8\">)</color> ";

	output += sn_simulating ?
		"<color=\"#95a2b8\">sim(</color> <color=\"#4287F5\">ACTIVE</color> <color=\"#95a2b8\">)</color> ":
		"<color=\"#95a2b8\">sim(</color> <color=\"#678AC2\">PAUSED</color> <color=\"#95a2b8\">)</color> ";

	VRCPlayerApi currentOwner = Networking.GetOwner(playerTotems[sn_turnid]);
	output += "<color=\"#95a2b8\">player(</color> <color=\"#4287F5\">"+ (currentOwner != null? currentOwner.displayName: "[null]") + ":" + sn_turnid + "</color> <color=\"#95a2b8\">)</color>";

	output += "\n---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------\n";

	// Update display 
	for( int i = 0; i < FRP_LEN ; i ++ )
	{
		output += FRP_LINES[ (FRP_MAX + FRP_PTR - FRP_LEN + i) % FRP_MAX ];
	}

	ltext.text = output;
}

}