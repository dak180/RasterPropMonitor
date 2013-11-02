using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace RasterPropMonitorGenerator
{
	public class RasterPropMonitorGenerator: InternalModule
	{
		[KSPField]
		public int refreshRate = 20;
		//[KSPField]
		public int refreshDataRate = 20;
		[KSPField]
		public string page1 = "Display$$$ not$$$  configured.";
		[KSPField]
		public string button1 = "";
		[KSPField]
		public string page2 = "";
		[KSPField]
		public string button2 = "";
		[KSPField]
		public string page3 = "";
		[KSPField]
		public string button3 = "";
		[KSPField]
		public string page4 = "";
		[KSPField]
		public string button4 = "";
		[KSPField]
		public string page5 = "";
		[KSPField]
		public string button5 = "";
		[KSPField]
		public string page6 = "";
		[KSPField]
		public string button6 = "";
		[KSPField]
		public string page7 = "";
		[KSPField]
		public string button7 = "";
		[KSPField]
		public string page8 = "";
		[KSPField]
		public string button8 = "";
		// Config syntax.
		private string[] lineSeparator = { Environment.NewLine };
		private string[] variableListSeparator = { "###" };
		private string[] variableSeparator = { "|" };
		private InternalModule targetScript;
		private string[] textArray;
		// Important pointers to the screen's data structures.
		FieldInfo remoteArray;
		FieldInfo remoteFlag;
		// Local variables
		private string[] pages = { "", "", "", "", "", "", "", "" };
		private int activePage = 0;
		private int charPerLine = 23;
		private int linesPerPage = 17;
		private string spacebuffer;
		private int updateCountdown = 0;
		private int dataUpdateCountdown = 0;
		private bool updateForced = false;
		private bool screenWasBlanked = false;
		// Local data fetching variables...
		private int gearGroupNumber;
		private int brakeGroupNumber;
		private int SASGroupNumber;
		private int lightGroupNumber;
		private int vesselNumParts;

		public void Start ()
		{
			// Mihara: We're getting at the screen module and it's parameters using reflection here.
			// While I would prefer to use some message passing mechanism instead,
			// it does not look like I can use KSPEvent.
			// I could directly lock at the parameters, seeing as how these two modules
			// are in the same assembly, but instead I'm leaving the reflection-based mechanism here
			// so that you could make your own screen driver module
			// by simply copy-pasting the relevant sections.
			foreach (InternalModule intModule in base.internalProp.internalModules) {
				if (intModule.ClassName == "RasterPropMonitor") {
					targetScript = intModule;
					remoteArray = intModule.GetType ().GetField ("screenText");
					remoteFlag = intModule.GetType ().GetField ("screenUpdateRequired");

					charPerLine = (int)intModule.GetType ().GetField ("screenWidth").GetValue (intModule);
					linesPerPage = (int)intModule.GetType ().GetField ("screenHeight").GetValue (intModule);

					break;
				}
			}

			spacebuffer = new String (' ', charPerLine);

			string[] pageData = new string[] { page1, page2, page3, page4, page5, page6, page7, page8 };
			string[] buttonName = new string[] { button1, button2, button3, button4, button5, button6, button7, button8 };
			for (int i=0; i<8; i++) {
				//Debug.Log ("RasterMonitor: Page " + i.ToString () + " data is \"" + pageData [i] + "\" button name is " + buttonName [i]);
				if (buttonName [i] != "") {
					GameObject buttonObject = base.internalProp.FindModelTransform (buttonName [i]).gameObject;
					buttonHandler pageButton = buttonObject.AddComponent<buttonHandler> ();
					pageButton.ID = i;
					pageButton.handlerFunction = buttonClick;
				}

				try {
					pages [i] = String.Join (Environment.NewLine, File.ReadAllLines (KSPUtil.ApplicationRootPath + "GameData/" + pageData [i], System.Text.Encoding.UTF8));
				} catch {
					// Notice that this will also happen if the referenced file is not found.
					pages [i] = pageData [i].Replace ("<=", "{").Replace ("=>", "}").Replace ("$$$", Environment.NewLine);
				}
			}


			textArray = new string[linesPerPage];
			for (int i = 0; i < textArray.Length; i++) {
				textArray [i] = "";
			}

			// Well, it looks like we have to do that bit just like in Firespitter.
			gearGroupNumber = BaseAction.GetGroupIndex (KSPActionGroup.Gear);
			brakeGroupNumber = BaseAction.GetGroupIndex (KSPActionGroup.Brakes);
			SASGroupNumber = BaseAction.GetGroupIndex (KSPActionGroup.SAS);
			lightGroupNumber = BaseAction.GetGroupIndex (KSPActionGroup.Light);
		}

		public void buttonClick (int buttonID)
		{
			activePage = buttonID;
			updateForced = true;
		}
		// Some snippets from MechJeb...
		private double ClampDegrees360 (double angle)
		{
			angle = angle % 360.0;
			if (angle < 0)
				return angle + 360.0;
			else
				return angle;
		}
		//keeps angles in the range -180 to 180
		private double ClampDegrees180 (double angle)
		{
			angle = ClampDegrees360 (angle);
			if (angle > 180)
				angle -= 360;
			return angle;
		}
		// Has quite a bit of MechJeb code which I barely understand.
		// Data common for various variable calculations
		Vector3d CoM;
		Vector3d up;
		Vector3d north;
		Quaternion rotationVesselSurface;
		Quaternion rotationSurface;
		Vector3d velocityVesselSurface;
		Vector3d velocityVesselOrbit;
		double speedVertical;

		ITargetable target;

		private void fetchCommonData ()
		{
			CoM = vessel.findWorldCenterOfMass ();
			up = (CoM - vessel.mainBody.position).normalized;
			north = Vector3d.Exclude (up, (vessel.mainBody.position + vessel.mainBody.transform.up * (float)vessel.mainBody.Radius) - CoM).normalized;
			rotationSurface = Quaternion.LookRotation (north, up);
			rotationVesselSurface = Quaternion.Inverse (Quaternion.Euler (90, 0, 0) * Quaternion.Inverse (vessel.GetTransform ().rotation) * rotationSurface);

			velocityVesselOrbit = vessel.orbit.GetVel();
			velocityVesselSurface = velocityVesselOrbit - vessel.mainBody.getRFrmVel(CoM);

			speedVertical = Vector3d.Dot(velocityVesselSurface, up);
			target = FlightGlobals.fetch.VesselTarget;
		}

		private Dictionary<string,Vector2d> resources;
		string[] resourcesAlphabetic;

		private void fetchPerPartData ()
		{
			resources = new Dictionary<string,Vector2d> ();
			foreach (Part part in vessel.parts) {
				// The cute way of using vector2d in place of a tuple is from Firespitter.
				// Hey, it works.
				foreach (PartResource resource in part.Resources) {
					if (!resources.ContainsKey ((resource.resourceName)))
						resources.Add (resource.resourceName, new Vector2d (resource.amount, resource.maxAmount));
					else
						resources [resource.resourceName] += new Vector2d (resource.amount, resource.maxAmount);
				}
			}
			resourcesAlphabetic = resources.Keys.ToArray ();
			Array.Sort (resourcesAlphabetic);
		}

		private double getResourceByName (string name)
		{
			Vector2d result;
			if (resources.TryGetValue (name, out result))
				return result.x;
			else
				return 0;
		}

		private double getMaxResourceByName (string name)
		{
			Vector2d result;
			if (resources.TryGetValue (name, out result))
				return result.y;
			else
				return 0;
		}

		private object processVariable (string input)
		{
			switch (input) {

			// It's a bit crude, but it's simple enough to populate.
			// Would be a bit smoother if I had eval() :)

			// Speeds.
			case "VERTSPEED":
				return speedVertical;
			case "SURFSPEED":
				return velocityVesselSurface.magnitude;
			case "ORBTSPEED":
				return velocityVesselOrbit.magnitude;
			case "TRGTSPEED":
				return FlightGlobals.ship_tgtSpeed;
			case "HORZVELOCITY":
					return (velocityVesselSurface - (speedVertical * up)).magnitude;
			// Altitudes
			case "ALTITUDE":
				return vessel.mainBody.GetAltitude (CoM);
			case "RADARALT":
				return vessel.altitude - Math.Max (vessel.pqsAltitude, 0D);

			// Orbital parameters
			case "ORBITBODY":
				return vessel.orbit.referenceBody.name;
			case "PERIAPSIS":
				return FlightGlobals.ship_orbit.PeA;
			case "APOAPSIS":
				return FlightGlobals.ship_orbit.ApA;
			case "INCLINATION":
				return FlightGlobals.ship_orbit.inclination;
			case "ECCENTRICITY":
				return vessel.orbit.eccentricity;
			// Time to apoapsis and periapsis are converted to DateTime objects and their formatting trickery applies.
			case "TIMETOAP":
				return new DateTime (TimeSpan.FromSeconds (vessel.orbit.timeToAp).Ticks); 
			case "TIMETOPE":
				if (vessel.orbit.eccentricity < 1)
					return new DateTime (TimeSpan.FromSeconds (vessel.orbit.timeToPe).Ticks);
				else
					return new DateTime (TimeSpan.FromSeconds (-vessel.orbit.meanAnomaly / (2 * Math.PI / vessel.orbit.period)).Ticks);

			// Coordinates.
			case "LATITUDE":
				return vessel.mainBody.GetLatitude (CoM);
			case "LONGITUDE":
				return ClampDegrees180 (vessel.mainBody.GetLongitude (CoM));

			// Orientation
			case "HEADING":
				return rotationVesselSurface.eulerAngles.y;
			case "PITCH":
				return (rotationVesselSurface.eulerAngles.x > 180) ? (360.0 - rotationVesselSurface.eulerAngles.x) : -rotationVesselSurface.eulerAngles.x;
			case "ROLL":
				return (rotationVesselSurface.eulerAngles.z > 180) ? (rotationVesselSurface.eulerAngles.z - 360.0) : rotationVesselSurface.eulerAngles.z;

			// Targeting. Probably the most finicky bit right now.
			case "TARGETNAME":
				if (target == null)
					return "";
				if (target is Vessel || target is CelestialBody)
					return target.GetName ();
					// Later, I think I want to get this to return the ship's name, not the docking node name...
				if (target is ModuleDockingNode)
					return target.GetName ();
				return "???!";
			case "TARGETDISTANCE":
				if (target != null) {
					return Vector3.Distance (target.GetTransform ().position, vessel.GetTransform ().position);
				} else
					return Double.NaN;
			case "RELATIVEINCLINATION":
				if (target != null) {
					Orbit targetorbit = target.GetOrbit ();
					if (targetorbit.referenceBody != vessel.orbit.referenceBody)
						return Double.NaN;
					// Not finished.
					return "Dunno...";
				} else
					return Double.NaN;
			
			// Resources by name.
			case "ELECTRIC":
				return getResourceByName ("ElectricCharge");
			case "ELECTRICMAX":
				return getMaxResourceByName ("ElectricCharge");
			case "FUEL":
				return getResourceByName ("LiquidFuel");
			case "FUELMAX":
				return getMaxResourceByName ("LiquidFuel");
			case "OXIDIZER":
				return getResourceByName ("Oxidizer");
			case "OXIDIZERMAX":
				return getMaxResourceByName ("Oxidizer");
			case "MONOPROP":
				return getResourceByName ("MonoPropellant");
			case "MONOPROPMAX":
				return getMaxResourceByName ("MonoPropellant");
			case "XENON":
				return getResourceByName ("XenonGas");
			case "XENONMAX":
				return getMaxResourceByName ("XenonGas");


			// Action group flags. If I got that right, upon entering string format it should get cast to something sensible...
			case "GEAR":
				return FlightGlobals.ActiveVessel.ActionGroups.groups [gearGroupNumber];
			case "BRAKES":
				return FlightGlobals.ActiveVessel.ActionGroups.groups [gearGroupNumber];
			case "SAS":
				return FlightGlobals.ActiveVessel.ActionGroups.groups [gearGroupNumber];
			case "LIGHTS":
				return FlightGlobals.ActiveVessel.ActionGroups.groups [gearGroupNumber];
			
			}
			// If input starts with "LISTR" we're handling it specially...
			// The variables are named like LISTR_<number>_<NAME|VAL|MAX>
			string[] tokens = input.Split ('_');

			if (tokens.Length == 3 && tokens [0] == "LISTR") {
				ushort resourceID = Convert.ToUInt16 (tokens [1]);
				switch (tokens [2]) {
				case "NAME":
					if (resourceID >= resources.Count)
						return "";
					else
						return resourcesAlphabetic [resourceID];
				case "VAL":
					if (resourceID >= resources.Count)
						return 0;
					else
						return resources [resourcesAlphabetic [resourceID]].x;
				case "MAX":
					if (resourceID >= resources.Count)
						return 0;
					else
						return resources [resourcesAlphabetic [resourceID]].y;
				}


			}

			// Didn't recognise anything so we return the string we got, that helps debugging.
			return input;
		}

		private string processString (string input)
		{
			// Each separate output line is delimited by Environment.NewLine.
			// When loading from a config file, you can't have newlines in it, so they're represented by "$$$".
			//
			// Within each line, if it contains any variables, it contains String.Format's format codes:
			// "Insert {0:0.0} variables {0:0.0} into this string###VARIABLE|VARIABLE"
			// 
			// <= has to be substituted for { and => for } when defining a screen in a config file.
			// It is much easier to write a text file and reference it by URL instead, writing 
			// screen definitions in a config file is only good enough for very small screens.
			// 
			// A more readable string format reference detailing where each variable is to be inserted and 
			// what it should look like can be found here: http://blog.stevex.net/string-formatting-in-csharp/

			if (input.IndexOf (variableListSeparator [0]) >= 0) {

				string[] tokens = input.Split (variableListSeparator, StringSplitOptions.RemoveEmptyEntries);
				if (tokens.Length != 2) {
					return "FORMAT ERROR";
				} else {
					string[] vars = tokens [1].Split (variableSeparator, StringSplitOptions.RemoveEmptyEntries);

					object[] variables = new object[vars.Length];
					for (int i=0; i<vars.Length; i++) {
						//Debug.Log ("PropMonitorGenerator: Processing " + vars[i]);
						variables [i] = processVariable (vars [i]);
					}
					return String.Format (tokens [0], variables);
				}
			} else
				return input;
		}
		// Update according to the given refresh rate or when number of parts changes.
		private bool updateCheck ()
		{
			if (vesselNumParts != vessel.Parts.Count || updateCountdown <= 0 || dataUpdateCountdown <= 0 || updateForced) {
				updateCountdown = refreshRate;
				if (vesselNumParts != vessel.Parts.Count || dataUpdateCountdown <= 0) {
					dataUpdateCountdown = refreshDataRate;
					vesselNumParts = vessel.Parts.Count;
					fetchPerPartData ();
				}
				updateForced = false;
				return true;
			} else {
				dataUpdateCountdown--;
				updateCountdown--;
				return false;
			}
		}

		public override void OnUpdate ()
		{
			if (!HighLogic.LoadedSceneIsFlight)
				return;

			if (!updateCheck ())
				return;

			if (CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA && vessel == FlightGlobals.ActiveVessel) {

				for (int i = 0; i < textArray.Length; i++)
					textArray [i] = spacebuffer;

				if (pages [activePage] == "") { // In case the page is empty, the screen is treated as turned off and blanked once.
					if (!screenWasBlanked) {
						screenWasBlanked = true;
						remoteArray.SetValue (targetScript, textArray);
						remoteFlag.SetValue (targetScript, true);
					}
				} else {
					fetchCommonData (); // Doesn't seem to be a better place to do it in...

					string[] linesArray = pages [activePage].Split (lineSeparator, StringSplitOptions.None);
					for (int i=0; i<linesArray.Length && i<linesPerPage; i++) {
						textArray [i] = processString (linesArray [i]) + spacebuffer;
					}
					remoteArray.SetValue (targetScript, textArray);
					remoteFlag.SetValue (targetScript, true);
					screenWasBlanked = false;
				}

			}
		}
	}

	public class buttonHandler:MonoBehaviour
	{
		public delegate void HandlerFunction (int ID);

		public HandlerFunction handlerFunction;
		public int ID;

		public void OnMouseDown ()
		{
			handlerFunction (ID);
		}
	}
}

