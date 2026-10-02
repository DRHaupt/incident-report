using IncidentReporting.Models;

namespace IncidentReporting.Templates;

public static partial class FormCatalog
{
    private static void AddHealthAndSafety(List<FormDefinition> list)
    {
        list.Add(Start("HS-200", "Employee Injury / Illness Report", "Health & Safety",
                "Work-related injury or illness, with fields supporting OSHA 300/301 recordkeeping.")
            .Include(f => CommonSections.PersonInvolved(f, "Injured Employee"))
            .Section("Injury Details")
            .Multi("body_parts", "Body Part(s) Affected", CommonSections.BodyParts)
            .Multi("injury_types", "Nature of Injury", CommonSections.InjuryTypes)
            .Choice("body_side", "Side of Body", "Left", "Right", "Both", "Not Applicable")
            .Choice("event_type", "Event / Exposure", true,
                "Slip / Trip / Fall (same level)", "Fall from Height", "Struck By Object", "Struck Against Object",
                "Caught In / Between", "Overexertion / Lifting", "Repetitive Motion", "Contact with Hot Object",
                "Contact with Chemical", "Contact with Electricity", "Vehicle Incident", "Assault", "Exposure to Noise", "Other")
            .Text("object_substance", "Object or Substance That Directly Harmed Employee")
            .Memo("task_performed", "Task Being Performed at Time of Injury")
            .Time("time_began_work", "Time Employee Began Work")
            .Section("Treatment")
            .Choice("treatment_level", "Highest Level of Treatment", true,
                "None", "First Aid Only", "Clinic / Physician", "Emergency Room", "Hospitalized Overnight", "Fatality")
            .Text("physician_name", "Physician / Health Care Professional")
            .Text("treatment_facility", "Treatment Facility Name & Address")
            .YesNo("er_visit", "Treated in Emergency Room?")
            .YesNo("inpatient", "Hospitalized Overnight as In-Patient?")
            .Section("Work Status & Recordability")
            .Choice("work_status", "Work Status", "Returned to Full Duty", "Restricted / Light Duty", "Days Away From Work", "Unknown")
            .Number("days_away", "Number of Days Away From Work")
            .Number("days_restricted", "Number of Days on Restriction / Transfer")
            .Date("return_to_work", "Return to Work Date")
            .YesNo("osha_recordable", "OSHA Recordable?")
            .Text("osha_case_no", "OSHA 300 Log Case Number")
            .YesNo("workers_comp_filed", "Workers' Compensation Claim Filed?")
            .Text("claim_number", "Claim Number")
            .YesNo("drug_test", "Post-Incident Drug / Alcohol Test Conducted?")
            .Section("PPE & Training")
            .Multi("ppe_worn", "PPE Worn",
                "Hard Hat", "Safety Glasses", "Face Shield", "Gloves", "Hearing Protection", "Respirator",
                "Safety Footwear", "High-Visibility Vest", "Fall Protection", "None")
            .YesNo("ppe_required", "Was PPE Required for the Task?")
            .YesNo("trained_task", "Was Employee Trained for the Task?")
            .YesNo("procedure_followed", "Was the Written Procedure Followed?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("HS-210", "Slip, Trip & Fall Report", "Health & Safety",
                "Fall incidents involving any person, including surface and environmental conditions.")
            .Include(CommonSections.PersonInvolved)
            .Section("Fall Details")
            .Choice("fall_type", "Type of Fall", true, "Slip", "Trip", "Fall Same Level", "Fall From Height", "Fall on Stairs", "Fall From Vehicle / Equipment")
            .Number("fall_height", "Approximate Fall Height (ft)")
            .Choice("surface_type", "Surface Type", "Carpet", "Tile", "Concrete", "Wood", "Asphalt", "Gravel", "Grass", "Stairs", "Ramp", "Ladder", "Other")
            .Multi("surface_condition", "Surface Condition",
                "Dry", "Wet", "Icy / Snow", "Oily / Greasy", "Uneven", "Damaged", "Debris / Clutter", "Loose Mat / Rug", "Cords / Hoses")
            .Choice("lighting", "Lighting", "Adequate", "Dim", "Dark", "Glare")
            .YesNo("warning_signs", "Were Warning Signs Posted?")
            .Text("footwear", "Footwear Worn")
            .YesNo("carrying_items", "Was the Person Carrying Items?")
            .YesNo("handrail_used", "Was a Handrail Available / Used?")
            .Section("Injury")
            .YesNo("injured", "Was the Person Injured?")
            .Multi("body_parts", "Body Part(s) Affected", CommonSections.BodyParts)
            .Choice("treatment_level", "Treatment", "Declined", "None Required", "First Aid", "Transported to Hospital", "Sought Own Care")
            .YesNo("photos_taken", "Were Photos of the Area Taken?")
            .YesNo("cctv_available", "Is CCTV Footage Available?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("HS-220", "Equipment / Machinery Incident Report", "Health & Safety",
                "Failure, malfunction or misuse of equipment, tools or machinery.")
            .Section("Equipment")
            .Text("equipment_name", "Equipment Name / Type", required: true)
            .Text("equipment_id", "Asset / Equipment ID")
            .Text("manufacturer", "Manufacturer")
            .Text("model_no", "Model Number")
            .Text("serial_no", "Serial Number")
            .Date("last_inspection", "Last Inspection / Service Date")
            .Section("Failure Details")
            .Choice("failure_mode", "Failure Mode", true,
                "Mechanical Failure", "Electrical Failure", "Hydraulic / Pneumatic Failure", "Software / Control Fault",
                "Guard Missing / Bypassed", "Operator Error", "Wear and Tear", "Improper Maintenance", "Unknown")
            .YesNo("lockout_applied", "Was Lockout / Tagout Applied?")
            .YesNo("equipment_tagged", "Has Equipment Been Tagged Out of Service?")
            .YesNo("injury_involved", "Did the Failure Result in Injury?")
            .Number("downtime_hours", "Downtime (hours)")
            .Number("repair_cost", "Estimated Repair Cost ($)")
            .Text("operator_name", "Operator Name")
            .YesNo("operator_certified", "Was Operator Certified / Authorized?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("HS-230", "Hazardous Material Exposure Report", "Health & Safety",
                "Exposure of a person to a chemical, biological, or physical agent.")
            .Include(f => CommonSections.PersonInvolved(f, "Exposed Person"))
            .Section("Exposure Details")
            .Text("substance", "Substance / Agent Name", required: true)
            .Text("cas_number", "CAS Number")
            .YesNo("sds_available", "Was the SDS Available?")
            .Multi("exposure_route", "Route of Exposure", "Inhalation", "Skin Contact", "Eye Contact", "Ingestion", "Injection / Puncture")
            .Number("exposure_duration", "Duration of Exposure (minutes)")
            .Text("concentration", "Estimated Concentration / Quantity")
            .Memo("symptoms", "Symptoms Reported")
            .YesNo("decontaminated", "Was Decontamination Performed?")
            .YesNo("eyewash_used", "Eyewash / Safety Shower Used?")
            .Choice("treatment_level", "Treatment", "None", "First Aid", "Occupational Health", "Emergency Room", "Hospitalized")
            .YesNo("poison_control", "Poison Control Contacted?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("HS-240", "Fire Incident Report", "Health & Safety",
                "Fire, smoke, explosion or false alarm event at a facility.")
            .Section("Fire Details")
            .Choice("fire_event", "Event Type", true, "Fire", "Smoke Only", "Explosion", "Alarm Activation - No Fire", "False Alarm", "Malicious Alarm")
            .Text("origin_point", "Point of Origin")
            .Choice("fire_cause", "Suspected Cause",
                "Electrical", "Cooking", "Smoking Materials", "Hot Work", "Heating Equipment", "Chemical Reaction", "Arson", "Lightning", "Unknown")
            .Multi("detection", "How Was It Detected?", "Smoke Detector", "Heat Detector", "Sprinkler Flow", "Manual Pull Station", "Person Observed", "Other")
            .YesNo("sprinklers_activated", "Did Sprinklers Activate?")
            .YesNo("extinguisher_used", "Was a Fire Extinguisher Used?")
            .Text("extinguisher_type", "Extinguisher Type / Location")
            .YesNo("building_evacuated", "Was the Building Evacuated?")
            .Number("evacuated_count", "Number of People Evacuated")
            .Number("evacuation_minutes", "Evacuation Time (minutes)")
            .YesNo("fire_dept_responded", "Did the Fire Department Respond?")
            .Text("fire_dept_incident_no", "Fire Department Incident Number")
            .Number("injuries_count", "Number of Injuries")
            .Number("damage_estimate", "Estimated Damage ($)")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("HS-250", "Ergonomic / Repetitive Strain Report", "Health & Safety",
                "Musculoskeletal discomfort or injury associated with workstation or task design.")
            .Include(f => CommonSections.PersonInvolved(f, "Affected Employee"))
            .Section("Ergonomic Assessment")
            .Multi("body_parts", "Area(s) of Discomfort", CommonSections.BodyParts)
            .Choice("onset", "Onset", "Sudden", "Gradual (days)", "Gradual (weeks)", "Gradual (months)")
            .Choice("workstation", "Workstation Type", "Office / Computer", "Assembly Line", "Warehouse / Material Handling", "Vehicle", "Lab", "Healthcare / Patient Handling", "Other")
            .Number("hours_per_day", "Hours per Day on Task")
            .Multi("risk_factors", "Risk Factors Present",
                "Repetitive Motion", "Awkward Posture", "Heavy Lifting", "Forceful Exertion", "Vibration",
                "Contact Stress", "Prolonged Standing", "Prolonged Sitting", "Poor Lighting", "Inadequate Breaks")
            .YesNo("assessment_requested", "Workstation Assessment Requested?")
            .Include(CommonSections.Footer)
            .Build());
    }
}
