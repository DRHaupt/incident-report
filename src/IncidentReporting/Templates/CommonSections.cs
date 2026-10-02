using IncidentReporting.Models;

namespace IncidentReporting.Templates;

/// <summary>Sections and option lists shared by every form template.</summary>
internal static class CommonSections
{
    public static readonly string[] Severity = { "Low", "Moderate", "High", "Critical" };

    public static readonly string[] Shift = { "Day", "Evening", "Night", "Weekend", "Not Applicable" };

    public static readonly string[] Gender = { "Female", "Male", "Non-binary", "Prefer not to say", "Unknown" };

    public static readonly string[] PersonType =
        { "Employee", "Contractor", "Temporary Worker", "Volunteer", "Visitor", "Customer", "Patient", "Resident", "Student", "Member of Public", "Other" };

    public static readonly string[] BodyParts =
    {
        "Head", "Face", "Eye(s)", "Ear(s)", "Neck", "Shoulder", "Upper Arm", "Elbow", "Forearm", "Wrist", "Hand",
        "Finger(s)", "Chest", "Abdomen", "Upper Back", "Lower Back", "Hip", "Groin", "Thigh", "Knee", "Lower Leg",
        "Ankle", "Foot", "Toe(s)", "Internal", "Multiple", "Other"
    };

    public static readonly string[] InjuryTypes =
    {
        "Abrasion / Scrape", "Amputation", "Bruise / Contusion", "Burn (Chemical)", "Burn (Thermal)", "Burn (Electrical)",
        "Concussion", "Crush", "Dislocation", "Fracture", "Laceration / Cut", "Puncture", "Sprain / Strain",
        "Foreign Body", "Hearing Loss", "Inhalation", "Needlestick", "Rash / Dermatitis", "Bite / Sting", "Other"
    };

    public static readonly string[] Notifications =
    {
        "Supervisor / Manager", "Safety Officer", "Human Resources", "Security", "Facilities", "Risk Management",
        "Legal", "Insurance Carrier", "Police", "Fire Department", "EMS", "Regulatory Agency", "Next of Kin", "Executive Leadership"
    };

    /// <summary>Standard header: what, when, where and who reported it.</summary>
    public static void Header(FormBuilder f)
    {
        f.Section("Incident Information")
            .Text(CommonKeys.Title, "Incident Title / Summary", required: true)
            .Date(CommonKeys.IncidentDate, "Date of Incident", required: true)
            .Time(CommonKeys.IncidentTime, "Time of Incident")
            .Choice("shift", "Shift", Shift)
            .Text(CommonKeys.Location, "Location / Address", required: true)
            .Text("specific_area", "Specific Area (room, floor, aisle)")
            .Text(CommonKeys.Department, "Department / Business Unit")
            .Choice(CommonKeys.Severity, "Severity", true, Severity)
            .Date("date_reported", "Date Reported");

        f.Section("Reporter Information")
            .Text(CommonKeys.ReporterName, "Reported By (Name)", required: true)
            .Text("reporter_title", "Job Title / Position")
            .Text("reporter_employee_id", "Employee ID")
            .Phone("reporter_phone", "Phone")
            .Email("reporter_email", "Email")
            .Text("supervisor_name", "Immediate Supervisor");
    }

    /// <summary>Person-involved block used by injury/medical-type forms.</summary>
    public static void PersonInvolved(FormBuilder f) => PersonInvolved(f, "Person Involved");

    public static void PersonInvolved(FormBuilder f, string title)
    {
        f.Section(title)
            .Text("person_name", "Full Name", required: true)
            .Choice("person_type", "Person Type", true, PersonType)
            .Date("person_dob", "Date of Birth")
            .Number("person_age", "Age")
            .Choice("person_gender", "Gender", Gender)
            .Text("person_address", "Home Address")
            .Phone("person_phone", "Phone")
            .Text("person_job_title", "Job Title (if employee)")
            .Date("person_hire_date", "Date of Hire (if employee)");
    }

    /// <summary>Narrative, witnesses, follow-up and sign-off appended to every form.</summary>
    public static void Footer(FormBuilder f)
    {
        f.Section("Description")
            .Memo("description", "Detailed Description of Incident", required: true,
                hint: "Describe what happened in sequence, including events leading up to the incident.")
            .Memo("immediate_actions", "Immediate Actions Taken")
            .Memo("contributing_factors", "Contributing Factors");

        f.Section("Witnesses")
            .YesNo("witnesses_present", "Were There Witnesses?")
            .Text("witness1_name", "Witness 1 - Name")
            .Phone("witness1_phone", "Witness 1 - Phone")
            .Memo("witness1_statement", "Witness 1 - Statement")
            .Text("witness2_name", "Witness 2 - Name")
            .Phone("witness2_phone", "Witness 2 - Phone")
            .Memo("witness2_statement", "Witness 2 - Statement");

        f.Section("Follow-Up")
            .Multi("notified", "Parties Notified", Notifications)
            .Memo("root_cause", "Root Cause")
            .Memo("corrective_actions", "Corrective / Preventive Actions")
            .Text("action_owner", "Action Owner")
            .Date("action_due", "Target Completion Date")
            .Choice("action_status", "Action Status", "Not Started", "In Progress", "Complete", "Not Required");

        f.Section("Sign-Off")
            .Signature("reporter_signature", "Reporter Signature (type full name)")
            .Date("reporter_signed", "Date Signed")
            .Text("reviewer_name", "Reviewed By")
            .Text("reviewer_title", "Reviewer Title")
            .Signature("reviewer_signature", "Reviewer Signature (type full name)")
            .Date("reviewer_signed", "Review Date")
            .Memo("reviewer_comments", "Reviewer Comments");
    }
}
