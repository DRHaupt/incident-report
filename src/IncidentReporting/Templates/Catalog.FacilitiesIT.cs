using IncidentReporting.Models;

namespace IncidentReporting.Templates;

public static partial class FormCatalog
{
    private static void AddFacilities(List<FormDefinition> list)
    {
        list.Add(Start("FAC-700", "Property Damage Report", "Property & Facilities",
                "Accidental damage to buildings, fixtures, furnishings or equipment.")
            .Section("Damage")
            .Choice("damage_type", "Type of Damage", true, "Impact / Collision", "Water", "Fire / Smoke", "Electrical", "Structural", "Accidental Breakage", "Pest", "Other")
            .Text("damaged_property", "Property Damaged", required: true)
            .Text("asset_tag", "Asset Tag")
            .Choice("property_owner", "Owner of Property", "Organization", "Landlord", "Tenant", "Employee", "Third Party")
            .Memo("damage_extent", "Extent of Damage")
            .Number("repair_estimate", "Repair / Replacement Estimate ($)")
            .YesNo("third_party_responsible", "Third Party Responsible?")
            .Text("third_party_name", "Responsible Party Name / Contact")
            .YesNo("area_safe", "Area Made Safe?")
            .YesNo("work_order_raised", "Maintenance Work Order Raised?")
            .Text("work_order_no", "Work Order Number")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("FAC-710", "Utility Outage / Building Systems Failure", "Property & Facilities",
                "Loss of power, water, HVAC, elevators, fire systems or other building services.")
            .Section("Outage")
            .Choice("system", "System Affected", true, "Electrical", "Water", "Gas", "HVAC", "Elevator", "Fire Alarm", "Sprinkler", "Telephone", "Network", "Sewer", "Generator", "Other")
            .DateTime("outage_start", "Outage Start", required: true)
            .DateTime("outage_end", "Service Restored")
            .Text("areas_affected", "Areas Affected")
            .YesNo("people_trapped", "Were People Trapped (e.g. elevator)?")
            .YesNo("utility_notified", "Utility Provider Notified?")
            .Text("utility_ticket", "Utility Ticket Number")
            .YesNo("fire_watch", "Fire Watch Established?")
            .YesNo("backup_operated", "Did Backup Systems Operate Correctly?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("FAC-720", "Water Leak / Flood Report", "Property & Facilities",
                "Internal water leaks, pipe bursts, roof leaks and flooding.")
            .Section("Leak")
            .Choice("leak_source", "Source", true, "Roof", "Pipe Burst", "Plumbing Fixture", "Sprinkler Head", "HVAC Condensate", "Water Heater", "Groundwater / Flood", "Unknown")
            .Choice("water_category", "Water Category", "Category 1 - Clean", "Category 2 - Grey", "Category 3 - Black / Sewage")
            .Text("rooms_affected", "Rooms / Floors Affected")
            .Number("sq_ft", "Approximate Area Affected (sq ft)")
            .YesNo("water_shutoff", "Water Shut Off?")
            .YesNo("restoration_called", "Restoration Contractor Called?")
            .YesNo("mold_risk", "Mold Assessment Required?")
            .Number("damage_estimate", "Estimated Damage ($)")
            .Include(CommonSections.Footer)
            .Build());
    }

    private static void AddInformationTechnology(List<FormDefinition> list)
    {
        list.Add(Start("IT-800", "Cybersecurity Incident Report", "Information Technology",
                "Malware, phishing, intrusion, denial of service and other information security events.")
            .Section("Incident Classification")
            .Choice("cyber_type", "Incident Type", true,
                "Phishing", "Malware / Virus", "Ransomware", "Unauthorized Access", "Account Compromise", "Denial of Service",
                "Insider Misuse", "Lost / Stolen Device", "Web Defacement", "Business Email Compromise", "Other")
            .DateTime("detected_at", "Date / Time Detected", required: true)
            .Choice("detected_by", "Detected By", "User Report", "Antivirus / EDR", "SIEM Alert", "Firewall / IDS", "Third Party", "Audit", "Other")
            .Section("Affected Systems")
            .Memo("systems_affected", "Systems / Hostnames Affected", required: true)
            .Text("ip_addresses", "IP Addresses Involved")
            .Text("accounts_affected", "User Accounts Affected")
            .Number("users_impacted", "Number of Users Impacted")
            .Multi("data_types", "Data Potentially Involved",
                "None", "Personal Information", "Health Information", "Payment Card Data", "Financial Records",
                "Credentials", "Intellectual Property", "Confidential Business Data")
            .YesNo("data_exfiltrated", "Evidence of Data Exfiltration?")
            .Section("Containment")
            .Multi("containment", "Containment Actions",
                "Device Isolated", "Account Disabled", "Password Reset", "Firewall Rule Added", "Email Purged",
                "System Restored from Backup", "Patch Applied", "Forensic Image Taken")
            .DateTime("contained_at", "Date / Time Contained")
            .Choice("ir_phase", "Response Phase", "Identification", "Containment", "Eradication", "Recovery", "Lessons Learned", "Closed")
            .YesNo("breach_notification", "Breach Notification Required?")
            .YesNo("law_enforcement", "Law Enforcement Notified?")
            .YesNo("cyber_insurance", "Cyber Insurance Carrier Notified?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("IT-810", "Data Privacy Breach Report", "Information Technology",
                "Unauthorized disclosure of personal or confidential information, electronic or paper.")
            .Section("Breach Details")
            .Choice("breach_type", "Type of Disclosure", true,
                "Misdirected Email", "Misdirected Mail / Fax", "Lost Paper Records", "Lost / Stolen Device",
                "Improper Disposal", "Unauthorized Viewing", "Verbal Disclosure", "System Misconfiguration", "Hacking", "Other")
            .DateTime("breach_discovered", "Date / Time Discovered", required: true)
            .Number("records_affected", "Number of Records / Individuals Affected")
            .Multi("data_elements", "Data Elements Involved",
                "Name", "Address", "Date of Birth", "SSN / National ID", "Driver's License", "Financial Account",
                "Payment Card", "Medical Information", "Health Insurance", "Username / Password", "Biometric", "Other")
            .YesNo("data_encrypted", "Was the Data Encrypted?")
            .YesNo("data_recovered", "Was the Data Recovered / Confirmed Destroyed?")
            .Section("Assessment & Notification")
            .Choice("harm_risk", "Risk of Harm", "Low", "Medium", "High")
            .YesNo("individuals_notified", "Affected Individuals Notified?")
            .Date("notification_date", "Notification Date")
            .YesNo("regulator_notified", "Regulator Notified?")
            .Text("regulator_name", "Regulator(s)")
            .Text("privacy_officer", "Privacy Officer Assigned")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("IT-820", "IT Service Outage Report", "Information Technology",
                "Unplanned outage or major degradation of an IT service.")
            .Section("Outage")
            .Text("service_name", "Service / Application", required: true)
            .Choice("priority", "Priority", true, "P1 - Critical", "P2 - High", "P3 - Medium", "P4 - Low")
            .DateTime("outage_start", "Outage Start", required: true)
            .DateTime("outage_end", "Service Restored")
            .Number("users_impacted", "Users Impacted")
            .Choice("cause_category", "Cause Category", "Hardware", "Software", "Network", "Change / Release", "Capacity", "Third-Party Provider", "Power", "Human Error", "Security", "Unknown")
            .Text("related_change", "Related Change Number")
            .Text("ticket_no", "Incident Ticket Number")
            .YesNo("sla_breached", "SLA Breached?")
            .Include(CommonSections.Footer)
            .Build());
    }
}
