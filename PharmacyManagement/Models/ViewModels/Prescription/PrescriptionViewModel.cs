namespace PharmacyManagement.Models.ViewModels.Prescription
{
    public class PrescriptionViewModel
    {
        public int PrescriptionId { get; set; }
        public int CustomerId { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public string FileUrl { get; set; } = string.Empty;
        public string? Notes { get; set; }
        public string? ExtractedMedicines { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime UploadedAt { get; set; }

        // Added to hold the parsed medicines and their stock availability
        public List<PrescriptionItemMatchViewModel> MatchedItems { get; set; } = new();
    }

    public class PrescriptionItemMatchViewModel
    {
        public string MedicineName { get; set; } = string.Empty;
        public string Dosage { get; set; } = string.Empty;
        public int? MedicineId { get; set; }
        public bool IsAvailable { get; set; }
        public int StockQuantity { get; set; }
        public decimal SellingPrice { get; set; }
    }
}