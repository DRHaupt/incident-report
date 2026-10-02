using IncidentReporting.Models;

namespace IncidentReporting.Templates;

public static partial class FormCatalog
{
    private static void AddGeneral(List<FormDefinition> list)
    {
        list.Add(Start("GEN-100", "General Incident Report", "General",
                "Catch-all report for any event that does not fit a more specific form.")
            .Section("Classification")
            .Choice("incident_category", "Incident Category", true,
                "Injury / Illness", "Property Damage", "Security", "Environmental", "Vehicle", "Equipment",
                "Behavioral", "Customer Complaint", "Policy Violation", "Other")
            .YesNo("injury_involved", "Was Anyone Injured?")
            .YesNo("property_damage", "Was Property Damaged?")
            .YesNo("police_involved", "Were Police Involved?")
            .Text("police_report_no", "Police Report Number")
            .Number("estimated_cost", "Estimated Cost of Loss ($)")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("GEN-110", "Near Miss Report", "General",
                "Records an unplanned event that did not result in injury or damage but had the potential to.")
            .Section("Near Miss Details")
            .Choice("near_miss_type", "Type of Near Miss", true,
                "Slip / Trip / Fall", "Struck By / Against", "Caught In / Between", "Falling Object", "Electrical",
                "Chemical", "Vehicle / Mobile Equipment", "Fire", "Ergonomic", "Other")
            .Choice("potential_outcome", "Most Likely Potential Outcome", true,
                "First Aid", "Medical Treatment", "Lost Time Injury", "Permanent Disability", "Fatality", "Property Damage Only")
            .Choice("likelihood", "Likelihood of Recurrence", "Rare", "Unlikely", "Possible", "Likely", "Almost Certain")
            .Memo("what_prevented", "What Prevented an Injury or Loss?")
            .YesNo("hazard_corrected", "Was the Hazard Corrected Immediately?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("GEN-120", "Hazard / Unsafe Condition Report", "General",
                "Reports an unsafe condition or practice observed before an incident occurs.")
            .Section("Hazard Details")
            .Choice("hazard_type", "Hazard Type", true,
                "Housekeeping", "Walking Surface", "Electrical", "Fire / Egress", "Machine Guarding", "Chemical",
                "Ergonomic", "Lighting", "Noise", "Structural", "Unsafe Behavior", "PPE", "Other")
            .Choice("risk_rating", "Risk Rating", true, "Low", "Medium", "High", "Extreme")
            .YesNo("area_isolated", "Has the Area Been Isolated / Tagged Out?")
            .Memo("recommended_fix", "Recommended Correction")
            .YesNo("reported_anonymously", "Reporter Wishes to Remain Anonymous")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("GEN-130", "Witness Statement", "General",
                "Standalone statement from a witness to an incident, linked by reference number.")
            .Section("Statement")
            .Link(CommonKeys.RelatedReport, "Related Incident Report No.")
            .Text("statement_name", "Witness Full Name", required: true)
            .Text("statement_address", "Address")
            .Phone("statement_phone", "Phone")
            .Choice("statement_relationship", "Relationship to Organization", CommonSections.PersonType)
            .Text("statement_position", "Witness Position at Time of Incident")
            .Memo("statement_text", "Statement (in witness's own words)", required: true)
            .Memo("statement_after", "What Happened Immediately After")
            .YesNo("statement_true", "Witness Affirms Statement is True and Accurate", required: true)
            .Signature("statement_signature", "Witness Signature (type full name)", required: true)
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("GEN-140", "Complaint / Grievance Report", "General",
                "Records a formal complaint from a customer, member of the public, or stakeholder.")
            .Section("Complainant")
            .Text("complainant_name", "Complainant Name", required: true)
            .Phone("complainant_phone", "Phone")
            .Email("complainant_email", "Email")
            .Text("complainant_address", "Address")
            .Choice("complaint_channel", "Received Via", "In Person", "Phone", "Email", "Letter", "Website", "Social Media", "Other")
            .Section("Complaint")
            .Choice("complaint_type", "Complaint Type", true,
                "Service Quality", "Staff Conduct", "Billing", "Product Quality", "Safety Concern", "Discrimination",
                "Accessibility", "Privacy", "Other")
            .Text("staff_involved", "Staff Member(s) Involved")
            .Memo("resolution_requested", "Resolution Requested by Complainant")
            .Memo("resolution_offered", "Resolution Offered")
            .Choice("complainant_satisfied", "Complainant Satisfied?", "Yes", "No", "Partially", "Unknown")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("GEN-150", "Lost & Found Property Report", "General",
                "Logs property that has been lost, found, claimed or disposed of.")
            .Section("Property")
            .Choice("lf_type", "Report Type", true, "Lost Item", "Found Item")
            .Text("lf_item", "Item Description", required: true)
            .Text("lf_brand", "Brand / Model")
            .Text("lf_color", "Color")
            .Text("lf_serial", "Serial Number / Identifying Marks")
            .Number("lf_value", "Estimated Value ($)")
            .Text("lf_storage", "Storage Location")
            .Text("lf_tag", "Property Tag Number")
            .Section("Owner / Finder")
            .Text("lf_owner_name", "Owner Name")
            .Phone("lf_owner_phone", "Owner Phone")
            .Text("lf_finder_name", "Found By")
            .Choice("lf_disposition", "Disposition", "Held", "Returned to Owner", "Turned Over to Police", "Donated", "Destroyed")
            .Date("lf_disposition_date", "Disposition Date")
            .Include(CommonSections.Footer)
            .Build());
    }
}
