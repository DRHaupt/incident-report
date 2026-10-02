using IncidentReporting.Models;

namespace IncidentReporting.Templates;

public static partial class FormCatalog
{
    private static readonly string[] SubjectRace =
        { "American Indian / Alaska Native", "Asian", "Black / African American", "Hispanic / Latino", "Native Hawaiian / Pacific Islander", "White", "Other", "Unknown" };

    private static void SubjectDescription(FormBuilder f)
    {
        f.Section("Subject Description")
            .Text("subject_name", "Subject Name (if known)")
            .Choice("subject_gender", "Gender", CommonSections.Gender)
            .Text("subject_age", "Approximate Age")
            .Choice("subject_race", "Race / Ethnicity", SubjectRace)
            .Text("subject_height", "Height")
            .Text("subject_weight", "Weight / Build")
            .Text("subject_hair", "Hair")
            .Memo("subject_clothing", "Clothing / Distinguishing Features")
            .Text("subject_vehicle", "Vehicle (make, model, color, plate)")
            .Text("subject_direction", "Direction of Travel");
    }

    private static void LawEnforcement(FormBuilder f)
    {
        f.Section("Law Enforcement")
            .YesNo("police_called", "Were Police Called?")
            .Time("police_arrival", "Police Arrival Time")
            .Text("police_agency", "Agency")
            .Text("officer_name", "Officer Name / Badge")
            .Text("police_report_no", "Police Report Number")
            .YesNo("arrest_made", "Was an Arrest Made?")
            .YesNo("cctv_available", "Is CCTV Footage Available?")
            .YesNo("cctv_preserved", "Has Footage Been Preserved?");
    }

    private static void AddSecurity(List<FormDefinition> list)
    {
        list.Add(Start("SEC-400", "Security Incident Report", "Security",
                "General security event including suspicious activity, disturbances and alarms.")
            .Section("Security Details")
            .Choice("security_type", "Incident Type", true,
                "Suspicious Person", "Suspicious Package", "Disturbance", "Alarm Activation", "Unauthorized Access",
                "Tailgating", "Lost / Stolen Badge", "Bomb Threat", "Protest / Demonstration", "Policy Violation", "Other")
            .Text("officer_responding", "Responding Officer")
            .Time("time_dispatched", "Time Dispatched")
            .Time("time_arrived", "Time Arrived")
            .Time("time_cleared", "Time Cleared")
            .Include(SubjectDescription)
            .Include(LawEnforcement)
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("SEC-410", "Theft / Loss Report", "Security",
                "Theft, burglary, robbery, or unexplained loss of organizational or personal property.")
            .Section("Loss Details")
            .Choice("theft_type", "Type", true, "Theft", "Burglary (forced entry)", "Robbery (force or threat)", "Shoplifting", "Embezzlement / Internal Theft", "Unexplained Loss")
            .Choice("property_owner", "Property Belongs To", "Organization", "Employee", "Customer / Visitor", "Vendor", "Other")
            .Memo("items_stolen", "Items Stolen (description, serial, quantity)", required: true)
            .Number("total_value", "Total Estimated Value ($)")
            .Text("point_of_entry", "Point of Entry")
            .YesNo("forced_entry", "Signs of Forced Entry?")
            .Date("last_seen_date", "Date Property Last Seen")
            .Text("last_seen_by", "Last Seen By")
            .YesNo("insurance_claim", "Insurance Claim to Be Filed?")
            .Include(SubjectDescription)
            .Include(LawEnforcement)
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("SEC-420", "Vandalism / Criminal Damage Report", "Security",
                "Deliberate damage, graffiti or defacement of property.")
            .Section("Damage")
            .Choice("vandalism_type", "Type", true, "Graffiti", "Broken Glass", "Damaged Fixtures", "Damaged Vehicle", "Arson", "Tampering", "Other")
            .Memo("damage_description", "Description of Damage", required: true)
            .Number("damage_estimate", "Estimated Repair Cost ($)")
            .YesNo("photos_taken", "Photos Taken?")
            .YesNo("area_secured", "Area Secured / Made Safe?")
            .Include(SubjectDescription)
            .Include(LawEnforcement)
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("SEC-430", "Trespass / Ban Notice Report", "Security",
                "Issues or documents a trespass warning or site ban.")
            .Include(SubjectDescription)
            .Section("Trespass Notice")
            .Text("subject_id_type", "Identification Type")
            .Text("subject_id_number", "Identification Number")
            .Text("subject_address", "Subject Address")
            .Choice("notice_type", "Notice Issued", true, "Verbal Warning", "Written Trespass Warning", "Ban - Temporary", "Ban - Permanent")
            .Date("ban_expires", "Ban Expiration Date")
            .Text("ban_scope", "Locations Covered by Notice")
            .YesNo("subject_signed", "Subject Signed Acknowledgment?")
            .YesNo("photo_on_file", "Photograph on File?")
            .Include(LawEnforcement)
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("SEC-440", "Use of Force Report", "Security",
                "Documents any physical intervention or use of force by staff.")
            .Section("Force Used")
            .Text("staff_involved", "Staff Member(s) Using Force", required: true)
            .Text("staff_credentials", "License / Certification Numbers")
            .Memo("reason_for_force", "Reason Force Was Necessary", required: true)
            .Multi("force_types", "Type(s) of Force",
                "Verbal Commands", "Escort / Guide", "Physical Restraint", "Takedown", "Handcuffs", "OC Spray",
                "Baton", "Conducted Energy Device", "Other")
            .Memo("deescalation", "De-escalation Attempted")
            .Number("restraint_minutes", "Duration of Restraint (minutes)")
            .YesNo("subject_injured", "Was the Subject Injured?")
            .YesNo("staff_injured", "Was Staff Injured?")
            .YesNo("medical_offered", "Was Medical Attention Offered?")
            .Include(SubjectDescription)
            .Include(LawEnforcement)
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("SEC-450", "Missing Person Report", "Security",
                "A patient, resident, student, child, or visitor reported missing on premises.")
            .Include(f => CommonSections.PersonInvolved(f, "Missing Person"))
            .Section("Missing Person Details")
            .Text("mp_height", "Height")
            .Text("mp_weight", "Weight")
            .Text("mp_hair", "Hair Color / Style")
            .Text("mp_eyes", "Eye Color")
            .Memo("mp_clothing", "Clothing When Last Seen", required: true)
            .DateTime("mp_last_seen", "Date / Time Last Seen", required: true)
            .Text("mp_last_location", "Last Known Location")
            .Text("mp_last_seen_by", "Last Seen By")
            .YesNo("mp_at_risk", "At-Risk Individual (medical, cognitive, minor)?")
            .Memo("mp_medical_needs", "Medical Needs / Medications")
            .Section("Search")
            .Time("search_started", "Search Initiated")
            .Memo("areas_searched", "Areas Searched")
            .YesNo("code_called", "Facility Code / Alert Called?")
            .YesNo("guardian_notified", "Family / Guardian Notified?")
            .Choice("mp_outcome", "Outcome", "Still Missing", "Located Safe", "Located - Injured", "Located - Deceased")
            .DateTime("mp_located", "Date / Time Located")
            .Text("mp_located_where", "Where Located")
            .Include(LawEnforcement)
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("SEC-460", "Threat / Bomb Threat Report", "Security",
                "Threats received by phone, email, letter or in person, including bomb threat checklist.")
            .Section("Threat Received")
            .Choice("threat_channel", "Received By", true, "Phone", "Email", "Letter / Note", "In Person", "Social Media", "Other")
            .Text("received_by", "Received By (staff name)")
            .Memo("exact_words", "Exact Words of Threat", required: true)
            .Section("Bomb Threat Checklist")
            .Text("bomb_when", "When Is the Bomb Going to Explode?")
            .Text("bomb_where", "Where Is It Right Now?")
            .Text("bomb_looks", "What Does It Look Like?")
            .Text("bomb_why", "Why Did You Place It?")
            .Multi("caller_voice", "Caller's Voice",
                "Male", "Female", "Calm", "Angry", "Excited", "Slow", "Rapid", "Soft", "Loud", "Accent", "Disguised", "Familiar")
            .Multi("background_sounds", "Background Sounds", "Street Noise", "Voices", "Music", "Machinery", "Office", "Vehicle", "Animal", "Static", "Quiet")
            .Text("caller_number", "Number Displayed / Return Address")
            .Number("call_length", "Length of Call (seconds)")
            .Section("Response")
            .YesNo("evacuated", "Was the Facility Evacuated?")
            .YesNo("search_conducted", "Was a Search Conducted?")
            .Include(LawEnforcement)
            .Include(CommonSections.Footer)
            .Build());
    }
}
