using IncidentReporting.Models;

namespace IncidentReporting.Templates;

public static partial class FormCatalog
{
    private static void AddVehicle(List<FormDefinition> list)
    {
        list.Add(Start("VEH-500", "Vehicle Accident Report", "Vehicle & Fleet",
                "Collision or accident involving an organization-owned, leased or personal vehicle used for work.")
            .Section("Our Vehicle")
            .Text("veh_unit", "Unit / Fleet Number")
            .Text("veh_year_make_model", "Year / Make / Model", required: true)
            .Text("veh_plate", "License Plate")
            .Text("veh_vin", "VIN")
            .Choice("veh_ownership", "Ownership", "Company Owned", "Leased", "Rental", "Personal Vehicle - Business Use")
            .Number("veh_odometer", "Odometer")
            .Memo("veh_damage", "Damage to Our Vehicle")
            .YesNo("veh_drivable", "Vehicle Drivable?")
            .Section("Driver")
            .Text("driver_name", "Driver Name", required: true)
            .Text("driver_license", "Driver's License Number")
            .Text("driver_license_state", "License State / Class")
            .Date("driver_license_exp", "License Expiration")
            .YesNo("driver_injured", "Driver Injured?")
            .YesNo("seatbelt", "Seatbelt Worn?")
            .YesNo("phone_in_use", "Phone / Device in Use?")
            .Number("passengers", "Number of Passengers")
            .Section("Other Party")
            .Text("other_driver", "Other Driver Name")
            .Phone("other_phone", "Other Driver Phone")
            .Text("other_address", "Other Driver Address")
            .Text("other_license", "Other Driver License Number")
            .Text("other_vehicle", "Other Vehicle Year / Make / Model")
            .Text("other_plate", "Other Vehicle Plate")
            .Text("other_insurer", "Other Party Insurance Company")
            .Text("other_policy", "Other Party Policy Number")
            .Memo("other_damage", "Damage to Other Vehicle / Property")
            .YesNo("other_injured", "Other Party Injured?")
            .Section("Conditions")
            .Choice("collision_type", "Collision Type", true,
                "Rear-End", "Head-On", "Sideswipe", "Angle / T-Bone", "Backing", "Parked Vehicle", "Fixed Object",
                "Pedestrian", "Cyclist", "Animal", "Rollover", "Single Vehicle", "Other")
            .Choice("road_condition", "Road Surface", "Dry", "Wet", "Snow", "Ice", "Gravel", "Mud", "Construction")
            .Choice("weather", "Weather", "Clear", "Cloudy", "Rain", "Snow", "Fog", "Sleet", "High Wind")
            .Choice("light_condition", "Light", "Daylight", "Dawn", "Dusk", "Dark - Lighted", "Dark - Unlighted")
            .Text("speed", "Estimated Speed (mph)")
            .Text("speed_limit", "Posted Speed Limit")
            .YesNo("citation_issued", "Citation Issued?")
            .Text("citation_to", "Citation Issued To / Violation")
            .Include(LawEnforcement)
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("VEH-510", "Forklift / Powered Industrial Truck Incident", "Vehicle & Fleet",
                "Incidents involving forklifts, pallet jacks, aerial lifts and other mobile equipment.")
            .Section("Equipment & Operator")
            .Choice("pit_type", "Equipment Type", true, "Sit-Down Forklift", "Stand-Up Reach Truck", "Order Picker", "Electric Pallet Jack", "Scissor Lift", "Boom Lift", "Tugger", "Other")
            .Text("pit_unit", "Unit Number")
            .Text("operator_name", "Operator Name", required: true)
            .YesNo("operator_certified", "Operator Certified?")
            .Date("cert_expires", "Certification Expiration")
            .YesNo("preshift_check", "Pre-Shift Inspection Completed?")
            .Section("Incident")
            .Choice("pit_event", "Event", true, "Struck Pedestrian", "Struck Racking", "Struck Structure", "Struck Vehicle", "Tip-Over", "Load Dropped", "Fell From Dock", "Mechanical Failure", "Other")
            .Text("load_description", "Load Being Carried")
            .Number("load_weight", "Approximate Load Weight (lbs)")
            .YesNo("racking_damaged", "Racking Damaged / Requires Inspection?")
            .Number("damage_estimate", "Estimated Damage ($)")
            .Include(CommonSections.Footer)
            .Build());
    }

    private static void AddEnvironmental(List<FormDefinition> list)
    {
        list.Add(Start("ENV-600", "Spill / Release Report", "Environmental",
                "Spill or release of oil, chemicals, or other materials to land, air, water or drains.")
            .Section("Release Details")
            .Text("material", "Material Released", required: true)
            .Text("material_cas", "CAS / UN Number")
            .Number("quantity", "Quantity Released")
            .Choice("quantity_units", "Units", "Gallons", "Liters", "Pounds", "Kilograms", "Cubic Feet", "Unknown")
            .Multi("release_medium", "Released To", "Indoor Floor", "Pavement", "Soil", "Storm Drain", "Sanitary Sewer", "Surface Water", "Groundwater", "Air")
            .Text("source", "Source of Release (tank, drum, line, vehicle)")
            .DateTime("release_stopped", "Date / Time Release Stopped")
            .YesNo("contained", "Was the Release Contained?")
            .YesNo("reached_water", "Did Material Reach Water or a Drain?")
            .Section("Response & Reporting")
            .Multi("cleanup_methods", "Cleanup Methods", "Absorbent Pads", "Booms", "Vacuum Truck", "Excavation", "Neutralization", "Contractor Response", "None Required")
            .Text("cleanup_contractor", "Cleanup Contractor")
            .Text("waste_disposal", "Waste Disposal Method / Manifest Number")
            .YesNo("reportable_quantity", "Exceeds Reportable Quantity?")
            .Multi("agencies_notified", "Agencies Notified", "National Response Center", "State Environmental Agency", "Local Emergency Planning Committee", "Fire Department", "Water Authority", "None")
            .Text("agency_case_no", "Agency Case / Report Number")
            .Number("cleanup_cost", "Cleanup Cost ($)")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("ENV-610", "Air Emission / Odor Complaint Report", "Environmental",
                "Unpermitted emissions, visible smoke, dust or odor complaints.")
            .Section("Emission")
            .Choice("emission_type", "Type", true, "Visible Emission", "Dust", "Odor", "Gas Leak", "Exceedance of Permit Limit", "Equipment Malfunction")
            .Text("emission_source", "Source / Stack / Equipment")
            .Text("permit_no", "Air Permit Number")
            .Number("emission_duration", "Duration (minutes)")
            .Choice("wind_direction", "Wind Direction", "N", "NE", "E", "SE", "S", "SW", "W", "NW", "Calm")
            .Text("complainant", "Complainant Name / Contact (if complaint)")
            .YesNo("agency_report_required", "Agency Report Required?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("ENV-620", "Severe Weather / Natural Event Damage Report", "Environmental",
                "Damage or disruption caused by storms, flooding, earthquake, wildfire and similar events.")
            .Section("Event")
            .Choice("weather_event", "Event Type", true, "Thunderstorm", "Tornado", "Hurricane / Tropical Storm", "Flood", "Winter Storm / Ice", "Earthquake", "Wildfire / Smoke", "Extreme Heat", "Hail", "Lightning Strike", "Landslide")
            .YesNo("warning_issued", "Was an Official Warning Issued?")
            .YesNo("shelter_used", "Shelter-in-Place / Evacuation Initiated?")
            .Number("people_affected", "Number of People Affected")
            .Section("Impact")
            .Memo("structural_damage", "Structural Damage")
            .Memo("equipment_damage", "Equipment / Inventory Damage")
            .YesNo("power_lost", "Loss of Power?")
            .Number("closure_hours", "Hours of Business Interruption")
            .Number("damage_estimate", "Estimated Damage ($)")
            .YesNo("insurance_notified", "Insurance Carrier Notified?")
            .Text("insurance_claim_no", "Insurance Claim Number")
            .Include(CommonSections.Footer)
            .Build());
    }
}
