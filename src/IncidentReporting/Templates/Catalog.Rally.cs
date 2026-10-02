using IncidentReporting.Models;

namespace IncidentReporting.Templates;

/// <summary>Stage rally and motorsport event forms.</summary>
public static partial class FormCatalog
{
    private const string RallyCategory = "Motorsport & Rally";

    private static readonly string[] RallyStageTypes =
        { "Special Stage", "Super Special Stage", "Shakedown", "Recce", "Road / Liaison Section", "Service Park", "Remote Service", "Refuel Zone", "Parc Fermé", "Regroup", "Time Control", "Spectator Area" };

    private static void RallyEvent(FormBuilder f)
    {
        f.Section("Event & Stage")
            .Text("rally_event", "Event Name", required: true)
            .Text("rally_round", "Championship / Round")
            .Choice("stage_type", "Location Type", true, RallyStageTypes)
            .Text("stage_number", "Stage Number (SS)")
            .Text("stage_name", "Stage Name")
            .Number("stage_km", "Distance Into Stage (km)")
            .Text("marshal_post", "Nearest Marshal Post / Radio Point / Split")
            .Text("gps", "GPS Coordinates / What3Words")
            .Text("clerk_of_course", "Clerk of the Course")
            .Text("stage_commander", "Stage Commander / Safety Officer");
    }

    private static void RallyCrew(FormBuilder f)
    {
        f.Section("Car & Crew")
            .Text("car_number", "Car Number", required: true)
            .Text("driver_name", "Driver", required: true)
            .Text("driver_licence", "Driver Competition Licence No.")
            .Text("codriver_name", "Co-Driver")
            .Text("codriver_licence", "Co-Driver Competition Licence No.")
            .Text("entrant", "Entrant / Team")
            .Text("car_class", "Class")
            .Text("car_make_model", "Car Make / Model");
    }

    private static void AddRally(List<FormDefinition> list)
    {
        list.Add(Start("RAL-1300", "Stage Incident (Car Off / Accident)", RallyCategory,
                "Competitor accident, car off the road, rollover, fire or stoppage on a stage.")
            .Include(RallyEvent)
            .Include(RallyCrew)
            .Section("Incident")
            .Choice("rally_incident_type", "Incident Type", true,
                "Off Road - No Impact", "Off Road - Impact", "Rollover", "Impact With Tree / Pole / Wall", "Collision With Another Competitor",
                "Fire", "Car Stopped on Stage - Mechanical", "Car Stopped on Stage - Damage", "Car in Water", "Struck Spectator / Official", "Struck Animal")
            .Choice("sos_ok", "Crew Signal Displayed", true, "OK Board", "SOS Board", "No Board Shown", "Not Observed")
            .YesNo("tracking_alert", "Safety Tracking Alert Received (e.g. impact / SOS)?")
            .Choice("car_position", "Car Position", "Blocking Road", "Partly on Road", "Off Road - Visible", "Off Road - Not Visible From Stage", "Removed From Stage")
            .Choice("warning_given", "Warning to Following Cars", "Warning Triangle Placed", "Marshal With Yellow Flag", "Co-Driver Warned Following Cars", "None")
            .Number("cars_passed", "Number of Cars Passed Before Stage Neutralized")
            .Section("Crew Status")
            .Choice("crew_status", "Crew Condition", true, "Both Crew OK", "Driver Injured", "Co-Driver Injured", "Both Injured", "Crew Trapped", "Unknown at Time of Report")
            .YesNo("extrication_required", "Was Extrication Required?")
            .YesNo("fire_suppression_used", "On-Board Fire Suppression Activated?")
            .YesNo("handheld_extinguisher_used", "Handheld Extinguisher Used?")
            .Section("Response")
            .YesNo("intervention_dispatched", "Intervention / Medical Vehicle Dispatched?")
            .Text("intervention_unit", "Unit(s) Dispatched (FIV / Rescue / Medical)")
            .Time("unit_dispatched", "Time Dispatched")
            .Time("unit_on_scene", "Time on Scene")
            .Choice("stage_action", "Stage Action", true, "Stage Continued", "Yellow Flag / Slow Zone", "Red Flag - Stage Stopped", "Stage Cancelled")
            .YesNo("crew_to_medical", "Crew Taken to Medical Centre / Hospital?")
            .Choice("car_status_after", "Car Status", "Continued in Event", "Retired", "Restarting Under Rally 2 / Super Rally", "Excluded")
            .YesNo("third_party_damage", "Third-Party Property Damaged?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("RAL-1310", "Rally Medical Intervention Report", RallyCategory,
                "Medical assessment and treatment of a competitor, official or spectator at a rally event.")
            .Include(RallyEvent)
            .Related()
            .Section("Patient")
            .Choice("patient_role", "Patient Is", true, "Driver", "Co-Driver", "Marshal / Official", "Service Crew", "Spectator", "Media", "Other")
            .Text("patient_name", "Name", required: true)
            .Text("patient_car_number", "Car Number (if competitor)")
            .Date("patient_dob", "Date of Birth")
            .Text("patient_blood_group", "Blood Group (from licence / overalls)")
            .Memo("patient_allergies", "Allergies / Medical Conditions")
            .Section("Assessment")
            .Text("medical_lead", "Lead Doctor / Paramedic", required: true)
            .Time("time_first_contact", "Time of First Patient Contact")
            .YesNo("helmet_removed", "Helmet Removed On Scene?")
            .Choice("extrication_method", "Extrication", "Self-Extricated", "Assisted Exit", "Extrication Seat / KED", "Cut-Out by Rescue Crew", "Not Required")
            .YesNo("spinal_precautions", "Spinal Precautions Applied?")
            .Text("gcs", "Glasgow Coma Scale (GCS)")
            .YesNo("loss_of_consciousness", "Loss of Consciousness?")
            .Choice("concussion_screen", "Concussion Screen", "Not Performed", "Negative", "Positive", "Inconclusive")
            .Text("vitals", "Vital Signs (HR / BP / SpO2 / RR)")
            .Memo("injuries_found", "Injuries / Findings", required: true)
            .Memo("treatment_given", "Treatment Given")
            .Section("Outcome")
            .Choice("medical_disposition", "Disposition", true, "Released On Scene", "Treated at Event Medical Centre", "Transported by Road Ambulance", "Air Ambulance / Helicopter", "Refused Treatment")
            .Text("receiving_hospital", "Receiving Hospital")
            .Choice("fitness_to_continue", "Fitness to Continue (competitors)", "Fit to Continue", "Not Fit - Withdrawn by Chief Medical Officer", "Fit Subject to Review", "Not Applicable")
            .YesNo("licence_endorsed", "Competition Licence Endorsed / Medical Suspension?")
            .Text("chief_medical_officer", "Chief Medical Officer Informed")
            .Include(CommonSections.SignOff)
            .Build());

        list.Add(Start("RAL-1320", "Spectator Safety Incident", RallyCategory,
                "Spectators in prohibited areas, refusing instructions, injured, or delaying a stage.")
            .Include(RallyEvent)
            .Section("Spectators")
            .Choice("spectator_issue", "Issue", true,
                "Spectators in Prohibited Area", "Refused Marshal Instructions", "Spectator Injured", "Spectator Struck by Competitor", "Spectator on Stage While Live",
                "Drone Flown Over Stage", "Spectator Vehicle Blocking Route", "Abuse of Officials", "Littering / Fire Risk")
            .Number("spectator_count", "Approximate Number of Spectators Involved")
            .Text("prohibited_zone", "Zone / Area Description")
            .Text("safety_car_passed", "Last Safety / Course Car Passed (0, 00, 000)")
            .YesNo("stage_delayed", "Was the Stage Delayed?")
            .Number("delay_minutes", "Delay (minutes)")
            .YesNo("stage_cancelled_spectators", "Was the Stage Cancelled Due to Spectators?")
            .YesNo("spectator_injured", "Was Any Spectator Injured?")
            .Text("injured_names", "Injured Spectator Name(s)")
            .Include(LawEnforcement)
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("RAL-1330", "Red Flag / Stage Stoppage Report", RallyCategory,
                "Stoppage of a running stage, including cars affected and notional time decisions.")
            .Include(RallyEvent)
            .Related()
            .Section("Stoppage")
            .Choice("red_flag_reason", "Reason", true, "Competitor Accident", "Blocked Road", "Spectator Safety", "Road Closure Breach", "Medical Intervention", "Fire", "Weather", "Timing / Communications Failure", "Other")
            .Time("red_flag_time", "Time Red Flag Shown / Stage Stopped", required: true)
            .Text("red_flag_authorized", "Authorized By (Clerk of the Course)", required: true)
            .Text("first_car_stopped", "First Car Stopped / Red Flagged")
            .Text("cars_red_flagged", "Car Numbers Shown Red Flag")
            .Text("cars_not_started", "Car Numbers Not Started")
            .Section("Resolution")
            .Choice("stage_resolution", "Outcome", true, "Stage Restarted", "Remaining Cars Rerouted", "Stage Cancelled", "Stage Neutralized - Cars Driven Through")
            .Time("stage_reopened", "Time Stage Reopened")
            .YesNo("notional_times", "Notional Times to Be Applied?")
            .Memo("notional_time_basis", "Basis for Notional Times")
            .YesNo("stewards_informed", "Stewards Informed?")
            .Include(CommonSections.Narrative)
            .Include(CommonSections.SignOff)
            .Build());

        list.Add(Start("RAL-1340", "Road Closure Breach / Unauthorized Vehicle on Stage", RallyCategory,
                "Public vehicle, pedestrian, cyclist or animal entering a closed stage.")
            .Include(RallyEvent)
            .Section("Breach")
            .Choice("breach_type", "Breach By", true, "Public Vehicle", "Pedestrian", "Cyclist", "Horse Rider", "Farm Machinery", "Emergency Vehicle (non-event)", "Livestock", "Wild Animal")
            .Text("entry_point", "Entry Point / Road Block", required: true)
            .YesNo("stage_live", "Was the Stage Live (after 000 car)?")
            .Text("roadblock_marshal", "Road Block Marshal on Duty")
            .YesNo("roadblock_manned", "Was the Road Block Manned at the Time?")
            .Text("breach_vehicle", "Vehicle Make / Model / Color")
            .Text("breach_plate", "Registration / Plate")
            .Text("breach_driver", "Driver / Person Name")
            .Text("closure_order", "Road Closure Order Reference")
            .Choice("breach_resolution", "How Resolved", "Escorted Off Stage", "Held Until Stage Closed", "Police Attended", "Left of Own Accord")
            .YesNo("breach_stage_stopped", "Did the Breach Cause the Stage to Be Stopped?")
            .Include(LawEnforcement)
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("RAL-1350", "Service Park / Refuel Zone Incident", RallyCategory,
                "Fuel spills, fires, speeding, unsafe working and injuries in service, refuel and parc fermé areas.")
            .Include(RallyEvent)
            .Section("Team")
            .Text("service_car_number", "Car Number", required: true)
            .Text("service_team", "Team / Entrant")
            .Text("service_bay", "Service Bay / Zone")
            .Section("Incident")
            .Choice("service_incident", "Incident Type", true,
                "Fuel Spill", "Fire", "Speeding in Service / Refuel Zone", "Engine Running During Refuel", "Crew Not Wearing Fire-Resistant Clothing",
                "Unsafe Jacking / Working Under Car", "Injury to Team Member", "Unauthorized Work in Parc Fermé", "Ground Sheet / Environmental Breach", "Unauthorized Person in Restricted Area")
            .Text("fuel_type", "Fuel Type")
            .Number("fuel_quantity", "Fuel Quantity Spilled (liters)")
            .YesNo("spill_kit_used", "Spill Kit Used?")
            .YesNo("extinguisher_used_service", "Extinguisher Used?")
            .Number("speed_limit", "Zone Speed Limit (km/h)")
            .Number("measured_speed", "Measured Speed (km/h)")
            .Text("measured_by", "Speed Measured By / Device")
            .YesNo("reported_to_stewards", "Reported to Stewards?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("RAL-1360", "Marshal / Official Incident Report", RallyCategory,
                "Injury, near miss, abuse or welfare issue involving a marshal or event official.")
            .Include(RallyEvent)
            .Section("Official")
            .Text("official_name", "Official / Marshal Name", required: true)
            .Text("official_role", "Role / Post")
            .Text("official_licence", "Marshal Licence / Grade")
            .Phone("official_phone", "Phone")
            .Number("hours_on_duty", "Hours on Duty at Time of Incident")
            .Section("Incident")
            .Choice("official_incident", "Incident Type", true,
                "Injury", "Near Miss With Competitor Car", "Struck by Competitor / Debris", "Verbal / Physical Abuse From Public", "Heat / Cold Exposure",
                "Fatigue", "Radio / Communications Failure", "Equipment Failure", "Vehicle Accident Travelling to Post", "Lost / Unaccounted For")
            .YesNo("in_safe_position", "Was the Marshal in a Designated Safe Position?")
            .Multi("official_ppe", "Equipment / PPE", "Tabard / Hi-Vis", "Whistle", "Flags", "Radio", "Extinguisher", "First Aid Kit", "Weather Gear")
            .YesNo("official_medical", "Medical Attention Required?")
            .YesNo("welfare_follow_up", "Welfare Follow-Up Arranged?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("RAL-1370", "Landowner / Third-Party Property Damage Claim", RallyCategory,
                "Damage to fences, walls, gates, crops, livestock, roads or signage caused during the event.")
            .Include(RallyEvent)
            .Section("Landowner")
            .Text("landowner_name", "Landowner / Claimant", required: true)
            .Text("landowner_address", "Address")
            .Phone("landowner_phone", "Phone")
            .Email("landowner_email", "Email")
            .Section("Damage")
            .Multi("damage_items", "Property Damaged", "Fence", "Wall", "Gate", "Hedge", "Trees", "Crops", "Livestock", "Road Surface", "Verge / Ditch", "Culvert / Drainage", "Signage", "Barrier / Guardrail", "Building", "Vehicle")
            .Memo("damage_detail", "Description of Damage", required: true)
            .Text("responsible_car", "Car Number Responsible (if known)")
            .YesNo("pre_event_photos", "Pre-Event Condition Photos Available?")
            .YesNo("post_event_photos", "Post-Event Photos Taken?")
            .Number("claim_amount", "Amount Claimed ($)")
            .Choice("claim_status", "Claim Status", "Received", "Under Assessment", "Repaired by Organizer", "Paid", "Referred to Insurer", "Disputed", "Closed")
            .Text("insurer_reference", "Insurer Reference")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("RAL-1380", "Competitor Conduct / Stewards Referral", RallyCategory,
                "Referral of a competitor to the stewards for a breach of the regulations.")
            .Include(RallyEvent)
            .Include(RallyCrew)
            .Related()
            .Section("Alleged Breach")
            .Choice("breach_category", "Category", true,
                "Speeding on Road Section", "Traffic Offence on Liaison", "Time Control Irregularity", "Ignoring Flag / Signal", "Failing to Stop / Assist at SOS",
                "Driving Wrong Way on Stage", "Unsporting Conduct", "Abuse of Officials", "Unauthorized Service / Assistance", "Parc Fermé Breach", "Recce Infringement")
            .Text("regulation_article", "Regulation / Article Allegedly Breached", required: true)
            .Text("referred_by", "Referred By (official / judge of fact)", required: true)
            .Multi("evidence_type", "Evidence", "Official's Report", "Tracking Data", "Time Card", "Video", "Photos", "Witness Statements", "Police Report")
            .Section("Hearing")
            .DateTime("summons_time", "Summons Issued")
            .DateTime("hearing_time", "Hearing Date / Time")
            .Text("stewards_panel", "Stewards Panel")
            .Choice("decision", "Decision", "Pending", "No Further Action", "Reprimand", "Fine", "Time Penalty", "Exclusion", "Referred to National Sporting Authority")
            .Text("penalty_detail", "Penalty Detail")
            .Text("decision_number", "Decision Document Number")
            .YesNo("appeal_lodged", "Intention to Appeal Lodged?")
            .Include(CommonSections.Narrative)
            .Include(CommonSections.SignOff)
            .Build());

        list.Add(Start("RAL-1390", "Scrutineering / Technical Non-Compliance", RallyCategory,
                "Safety equipment or technical non-compliance found at scrutineering or post-event inspection.")
            .Include(RallyEvent)
            .Include(RallyCrew)
            .Section("Inspection")
            .Choice("scrutineering_stage", "Inspection", true, "Pre-Event Scrutineering", "Random Check During Event", "Post-Event Inspection", "Fuel Sample", "Weight Check", "Protest Inspection")
            .Text("scrutineer", "Scrutineer", required: true)
            .Multi("noncompliant_items", "Item(s) Non-Compliant",
                "Roll Cage", "Seats", "Harnesses", "Plumbed-In Extinguisher", "Handheld Extinguisher", "Helmet", "FHR / HANS Device", "Overalls / Underwear",
                "Fuel Cell / Lines", "Battery Mounting", "Towing Eyes", "Warning Triangles / SOS Boards", "Tyres", "Homologation Papers", "Safety Tracking Unit", "First Aid Kit")
            .Memo("noncompliance_detail", "Details of Non-Compliance", required: true)
            .Text("homologation_ref", "Homologation / Standard Reference")
            .Choice("scrutineering_action", "Action", true, "Rectified and Re-Presented", "Start Refused", "Reported to Stewards", "Seal Applied for Later Inspection", "Advisory Only")
            .Time("re_inspected", "Time Re-Inspected")
            .Include(CommonSections.SignOff)
            .Build());

        list.Add(Start("RAL-1400", "Rally Vehicle Recovery Log", RallyCategory,
                "Recovery of a stranded or damaged competition car from a stage or road section.")
            .Include(RallyEvent)
            .Include(RallyCrew)
            .Related()
            .Section("Recovery")
            .Time("recovery_requested", "Time Recovery Requested", required: true)
            .Text("recovery_unit", "Recovery Vehicle / Operator", required: true)
            .Choice("recovery_timing", "Recovery Carried Out", "During Stage (stage stopped)", "After Sweep / Stage Closed", "After Event")
            .Time("recovery_arrived", "Time Recovery Arrived")
            .Time("recovery_cleared", "Time Cleared From Stage")
            .Choice("car_condition", "Car Condition", "Drivable", "Towable", "Requires Flatbed / Winch", "Fire Damaged", "In Water / Inaccessible")
            .Choice("car_taken_to", "Car Taken To", "Service Park", "Stage Exit / Holding Area", "Team Trailer", "Off-Site Storage", "Left In Situ")
            .Memo("recovery_damage", "Damage Noted at Recovery (incl. any caused by recovery)")
            .YesNo("fluids_leaking", "Fuel / Oil Leaking?")
            .Signature("crew_acknowledgment", "Crew / Team Acknowledgment (type full name)")
            .Include(CommonSections.SignOff)
            .Build());
    }
}
