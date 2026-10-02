using IncidentReporting.Models;

namespace IncidentReporting.Templates;

public static partial class FormCatalog
{
    private static void AddHumanResources(List<FormDefinition> list)
    {
        list.Add(Start("HR-900", "Workplace Violence Report", "Human Resources",
                "Physical assault, threats, intimidation or other violent behavior in the workplace.")
            .Section("Incident")
            .Choice("violence_type", "Type", true, "Physical Assault", "Verbal Threat", "Written / Electronic Threat", "Intimidation", "Stalking", "Weapon Displayed", "Property Destruction", "Domestic Violence Spillover")
            .Choice("perpetrator_type", "Perpetrator Relationship", "Employee", "Former Employee", "Customer / Client", "Patient", "Visitor", "Domestic Partner", "Stranger", "Unknown")
            .YesNo("weapon_involved", "Was a Weapon Involved?")
            .Text("weapon_type", "Weapon Type")
            .Text("victim_names", "Victim(s)", required: true)
            .Text("perpetrator_name", "Perpetrator Name (if known)")
            .YesNo("injuries", "Were There Injuries?")
            .YesNo("protective_order", "Is There a Protective / Restraining Order?")
            .YesNo("eap_offered", "Employee Assistance Program Offered?")
            .Include(LawEnforcement)
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("HR-910", "Harassment / Discrimination Complaint", "Human Resources",
                "Confidential complaint of harassment, discrimination, bullying or retaliation.")
            .Section("Complainant")
            .Text("complainant_name", "Complainant Name", required: true)
            .Text("complainant_title", "Job Title")
            .Text("complainant_dept", "Department")
            .Phone("complainant_phone", "Preferred Phone")
            .Email("complainant_email", "Preferred Email")
            .Section("Allegation")
            .Multi("allegation_type", "Nature of Complaint",
                "Sexual Harassment", "Racial Harassment", "Bullying", "Discrimination - Race", "Discrimination - Sex / Gender",
                "Discrimination - Age", "Discrimination - Disability", "Discrimination - Religion", "Discrimination - National Origin",
                "Discrimination - Sexual Orientation", "Retaliation", "Hostile Work Environment", "Other")
            .Text("respondent_name", "Person(s) Complained About", required: true)
            .Text("respondent_title", "Respondent Title / Relationship")
            .Choice("frequency", "Frequency", "Single Occurrence", "Multiple Occurrences", "Ongoing")
            .Date("first_occurrence", "Date of First Occurrence")
            .Date("last_occurrence", "Date of Most Recent Occurrence")
            .YesNo("previously_reported", "Previously Reported?")
            .Text("previously_reported_to", "Previously Reported To / When")
            .Memo("evidence", "Documents or Evidence Available")
            .Memo("desired_outcome", "Desired Outcome")
            .Section("Investigation")
            .Text("investigator", "Investigator Assigned")
            .Date("investigation_start", "Investigation Start Date")
            .YesNo("interim_measures", "Interim Measures Taken?")
            .Memo("interim_details", "Interim Measure Details")
            .Choice("finding", "Finding", "Pending", "Substantiated", "Partially Substantiated", "Unsubstantiated", "Inconclusive")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("HR-920", "Policy Violation / Misconduct Report", "Human Resources",
                "Breach of organizational policy, code of conduct, or work rules.")
            .Section("Violation")
            .Text("employee_name", "Employee Name", required: true)
            .Text("employee_id", "Employee ID")
            .Text("policy_violated", "Policy / Rule Violated", required: true)
            .Choice("violation_category", "Category", "Attendance", "Safety Rule", "Conduct", "Substance Use", "Theft / Fraud", "Confidentiality", "Use of Technology", "Conflict of Interest", "Other")
            .YesNo("prior_violations", "Prior Violations on Record?")
            .Choice("action_taken", "Action Taken", "None", "Coaching", "Verbal Warning", "Written Warning", "Final Warning", "Suspension", "Termination", "Pending Investigation")
            .YesNo("employee_acknowledged", "Employee Acknowledged in Writing?")
            .Include(CommonSections.Footer)
            .Build());
    }

    private static void AddCareAndEducation(List<FormDefinition> list)
    {
        list.Add(Start("CARE-1000", "Patient / Resident Safety Event", "Healthcare & Care",
                "Adverse events, near misses and unsafe conditions affecting patients or residents.")
            .Include(f => CommonSections.PersonInvolved(f, "Patient / Resident"))
            .Section("Clinical Details")
            .Text("mrn", "Medical Record / Resident Number")
            .Text("unit_room", "Unit / Room / Bed")
            .Text("admitting_dx", "Admitting Diagnosis")
            .Choice("event_category", "Event Category", true,
                "Fall", "Medication", "Treatment / Procedure", "Pressure Injury", "Infection", "Equipment / Device",
                "Diagnostic / Lab", "Patient Identification", "Elopement", "Self-Harm", "Patient Behavior", "Skin Tear", "Other")
            .Choice("harm_level", "Harm Level", true,
                "Unsafe Condition", "Near Miss - Did Not Reach Patient", "No Harm", "Mild Harm", "Moderate Harm", "Severe Harm", "Death")
            .YesNo("physician_notified", "Physician Notified?")
            .Text("physician_name", "Physician Name")
            .Time("physician_time", "Time Notified")
            .YesNo("family_notified", "Family / Representative Notified?")
            .YesNo("disclosure", "Event Disclosed to Patient / Family?")
            .Section("Fall Assessment (if applicable)")
            .YesNo("fall_risk_assessed", "Fall Risk Assessed on Admission?")
            .Choice("fall_risk_level", "Fall Risk Level", "Low", "Moderate", "High")
            .Multi("fall_precautions", "Precautions in Place", "Bed Alarm", "Chair Alarm", "Low Bed", "Floor Mats", "Bed Rails", "Non-Slip Socks", "Hourly Rounding", "Sitter", "Call Light in Reach")
            .Choice("found_position", "Patient Found", "Witnessed Fall", "On Floor", "Lowered to Floor by Staff", "Assisted Fall")
            .YesNo("head_strike", "Possible Head Strike?")
            .YesNo("anticoagulants", "On Anticoagulants?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("CARE-1010", "Medication Error Report", "Healthcare & Care",
                "Errors in prescribing, dispensing, administering or monitoring medication.")
            .Include(f => CommonSections.PersonInvolved(f, "Patient / Resident"))
            .Section("Medication")
            .Text("med_name", "Medication Name", required: true)
            .Text("med_dose_ordered", "Dose Ordered")
            .Text("med_dose_given", "Dose Given")
            .Choice("med_route", "Route", "Oral", "IV", "IM", "Subcutaneous", "Topical", "Inhaled", "Rectal", "Sublingual", "Transdermal", "Other")
            .Choice("error_type", "Type of Error", true,
                "Wrong Patient", "Wrong Drug", "Wrong Dose", "Wrong Route", "Wrong Time", "Omission", "Extra Dose",
                "Wrong Formulation", "Expired Drug", "Allergy Overlooked", "Documentation Error", "Monitoring Error")
            .Choice("error_stage", "Stage", "Prescribing", "Transcribing", "Dispensing", "Administering", "Monitoring")
            .Choice("ncc_merp", "NCC MERP Category",
                "A - Circumstances with capacity to cause error", "B - Error did not reach patient", "C - Reached patient, no harm",
                "D - Required monitoring", "E - Temporary harm, intervention", "F - Temporary harm, hospitalization",
                "G - Permanent harm", "H - Intervention to sustain life", "I - Death")
            .Text("staff_involved", "Staff Involved")
            .YesNo("prescriber_notified", "Prescriber Notified?")
            .YesNo("pharmacy_notified", "Pharmacy Notified?")
            .Memo("patient_outcome", "Patient Outcome / Monitoring")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("EDU-1100", "Student / Child Injury Report", "Education & Childcare",
                "Injuries to students or children in school, daycare, camp or youth programs.")
            .Include(f => CommonSections.PersonInvolved(f, "Student / Child"))
            .Section("Student Details")
            .Text("grade_class", "Grade / Classroom")
            .Text("teacher", "Teacher / Supervising Staff")
            .Choice("activity", "Activity at Time of Injury", "Classroom", "Recess / Playground", "Physical Education", "Athletics", "Field Trip", "Arrival / Dismissal", "Bus", "Lunch", "Other")
            .Text("equipment_involved", "Playground / Sports Equipment Involved")
            .Text("staff_ratio", "Staff-to-Child Ratio at Time")
            .Section("Injury & Care")
            .Multi("body_parts", "Body Part(s)", CommonSections.BodyParts)
            .Multi("injury_types", "Nature of Injury", CommonSections.InjuryTypes)
            .Multi("care_given", "Care Given", "Ice", "Bandage", "Cleaned Wound", "Rest", "Observed", "Nurse Evaluated", "EMS Called")
            .YesNo("head_injury", "Head Injury? (send head-injury notice home)")
            .Section("Parent / Guardian")
            .Text("guardian_name", "Parent / Guardian Name")
            .Phone("guardian_phone", "Phone")
            .Choice("guardian_contact_method", "Contact Method", "Phone", "In Person", "Text", "Email", "Note Home", "Unable to Reach")
            .Time("guardian_contact_time", "Time Contacted")
            .Choice("student_outcome", "Outcome", "Returned to Class", "Picked Up by Guardian", "Transported to Hospital", "Other")
            .Signature("guardian_signature", "Guardian Acknowledgment (type full name)")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("EDU-1110", "Behavioral Incident Report", "Education & Childcare",
                "Disruptive, aggressive or unsafe behavior by a student, client or participant.")
            .Include(f => CommonSections.PersonInvolved(f, "Individual"))
            .Section("Behavior")
            .Multi("behaviors", "Behavior(s) Observed",
                "Physical Aggression", "Verbal Aggression", "Property Destruction", "Self-Injury", "Elopement / Running",
                "Defiance", "Bullying", "Inappropriate Language", "Sexualized Behavior", "Substance Possession", "Weapon Possession")
            .Memo("antecedent", "Antecedent (what happened before)", required: true)
            .Memo("behavior_detail", "Behavior (what the individual did)", required: true)
            .Memo("consequence", "Consequence (staff response)")
            .Number("behavior_duration", "Duration (minutes)")
            .YesNo("restraint_used", "Restraint or Seclusion Used?")
            .Number("restraint_minutes", "Restraint / Seclusion Duration (minutes)")
            .YesNo("bip_in_place", "Behavior Intervention Plan in Place?")
            .YesNo("bip_followed", "Was the Plan Followed?")
            .Choice("discipline", "Disciplinary Outcome", "None", "Conference", "Detention", "Loss of Privilege", "In-School Suspension", "Out-of-School Suspension", "Referral to Administration")
            .YesNo("guardian_notified", "Parent / Guardian Notified?")
            .Include(CommonSections.Footer)
            .Build());
    }

    private static void AddHospitalityAndRetail(List<FormDefinition> list)
    {
        list.Add(Start("CUS-1200", "Customer / Visitor Injury Report", "Hospitality & Retail",
                "Injury or illness involving a customer, guest or visitor on premises. Use for liability claims.")
            .Include(f => CommonSections.PersonInvolved(f, "Customer / Visitor"))
            .Section("Injury")
            .Memo("claimed_injury", "Injury Claimed by Customer", required: true)
            .Multi("body_parts", "Body Part(s)", CommonSections.BodyParts)
            .YesNo("visible_injury", "Was Injury Visible?")
            .Choice("treatment_level", "Treatment", "Declined", "First Aid", "EMS Called - Refused", "Transported by EMS", "Left to Seek Own Care")
            .Section("Liability")
            .Text("receipt_no", "Transaction / Receipt Number")
            .Text("product_involved", "Product Involved (SKU)")
            .YesNo("customer_statement", "Customer Gave Written Statement?")
            .YesNo("photos_taken", "Photos Taken?")
            .YesNo("cctv_preserved", "CCTV Preserved?")
            .YesNo("admission_made", "Did Staff Make Any Admission of Fault? (should be No)")
            .YesNo("insurer_notified", "Liability Insurer Notified?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("CUS-1210", "Food Safety Incident Report", "Hospitality & Retail",
                "Suspected foodborne illness, foreign object, allergen or temperature control failure.")
            .Section("Food Safety Event")
            .Choice("food_event", "Event Type", true, "Suspected Foodborne Illness", "Foreign Object", "Allergen Exposure", "Temperature Abuse", "Pest Sighting", "Expired Product", "Cross-Contamination", "Recall")
            .Text("food_item", "Food / Product Involved", required: true)
            .Text("lot_number", "Lot / Batch Number")
            .Date("consumed_date", "Date Consumed / Purchased")
            .Number("people_ill", "Number of People Reporting Illness")
            .Memo("symptoms", "Symptoms Reported")
            .Text("allergen", "Allergen Involved")
            .YesNo("product_held", "Product Held / Quarantined?")
            .YesNo("sample_retained", "Sample Retained for Testing?")
            .YesNo("health_dept_notified", "Health Department Notified?")
            .Text("guest_name", "Guest / Customer Name")
            .Phone("guest_phone", "Guest Phone")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("CUS-1220", "Cash Discrepancy / Fraud Report", "Hospitality & Retail",
                "Till shortages, counterfeit currency, card fraud and refund abuse.")
            .Section("Discrepancy")
            .Choice("fraud_type", "Type", true, "Till Shortage", "Till Overage", "Counterfeit Currency", "Card Fraud / Chargeback", "Refund Fraud", "Gift Card Fraud", "Check Fraud", "Other")
            .Text("register_no", "Register / Terminal Number")
            .Text("cashier", "Cashier / Employee")
            .Number("expected_amount", "Expected Amount ($)")
            .Number("actual_amount", "Actual Amount ($)")
            .Number("variance", "Variance ($)")
            .Text("transaction_refs", "Transaction References")
            .YesNo("counterfeit_retained", "Counterfeit Note Retained?")
            .Include(SubjectDescription)
            .Include(LawEnforcement)
            .Include(CommonSections.Footer)
            .Build());
    }
}
