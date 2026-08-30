using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PharmacyManagement.Data;
using PharmacyManagement.Models;
using PharmacyManagement.Models.ViewModels.Cart;
using PharmacyManagement.Models.ViewModels.Prescription;
using PharmacyManagement.Models.ViewModels.Purchase;
using PharmacyManagement.Services;

namespace PharmacyManagement.Controllers
{
    [Authorize(Roles = "Customer")]
    public class CustomerController : Controller
    {
        private readonly AppDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IWebHostEnvironment _webHostEnvironment;
        private readonly GroqPrescriptionService _groqService;

        public CustomerController(
            AppDbContext context,
            UserManager<ApplicationUser> userManager,
            IWebHostEnvironment webHostEnvironment,
            GroqPrescriptionService groqService)
        {
            _context = context;
            _userManager = userManager;
            _webHostEnvironment = webHostEnvironment;
            _groqService = groqService;
        }

        // MY PRESCRIPTIONS & INVENTORY MATCHING
        
        [HttpGet]
        public async Task<IActionResult> MyPrescriptions()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null || user.CustomerId == null)
            {
                return Unauthorized();
            }

            var presList = await _context.Prescriptions
                .Where(p => p.CustomerId == user.CustomerId.Value)
                .OrderByDescending(p => p.UploadedAt)
                .ToListAsync();

            var allMedicines = await _context.Medicines
                .Include(m => m.Stocks)
                .ToListAsync();

            var prescriptions = new List<PrescriptionViewModel>();

            foreach (var p in presList)
            {
                var matchedItems = new List<PrescriptionItemMatchViewModel>();

                if (!string.IsNullOrEmpty(p.ExtractedMedicines))
                {
                    foreach (var med in allMedicines)
                    {
                        if (!string.IsNullOrEmpty(med.Name) &&
                            p.ExtractedMedicines.Contains(med.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            int totalStock = med.Stocks?.Sum(s => s.Quantity) ?? 0;
                            decimal latestPrice = med.Stocks?
                                .OrderByDescending(s => s.CreatedAt)
                                .FirstOrDefault()?.SellingPrice ?? 0.00m;

                            matchedItems.Add(new PrescriptionItemMatchViewModel
                            {
                                MedicineName = med.Name,
                                Dosage = "As directed",
                                MedicineId = med.MedicineId,
                                IsAvailable = totalStock > 0,
                                StockQuantity = totalStock,
                                SellingPrice = latestPrice
                            });
                        }
                    }
                }

                // Determine stock availability text based on AI detection & inventory match
                string inventoryStatus = p.Status ?? "Pending";
                if (!string.IsNullOrEmpty(p.ExtractedMedicines))
                {
                    if (matchedItems.Any())
                    {
                        bool allAvailable = matchedItems.All(m => m.IsAvailable);
                        bool anyAvailable = matchedItems.Any(m => m.IsAvailable);

                        if (allAvailable)
                        {
                            inventoryStatus = "All Available";
                        }
                        else if (anyAvailable)
                        {
                            inventoryStatus = "Partially Available";
                        }
                        else
                        {
                            inventoryStatus = "Out of Stock";
                        }
                    }
                    else
                    {
                        inventoryStatus = "No Catalog Match";
                    }
                }

                prescriptions.Add(new PrescriptionViewModel
                {
                    PrescriptionId = p.PrescriptionId,
                    CustomerId = p.CustomerId,
                    FileUrl = p.FileUrl ?? string.Empty,
                    Notes = p.Notes,
                    ExtractedMedicines = p.ExtractedMedicines,
                    Status = inventoryStatus,
                    UploadedAt = p.UploadedAt,
                    MatchedItems = matchedItems
                });
            }

            return View("~/Views/Customer/MyPrescriptions.cshtml", prescriptions);
        }

        // UPLOAD PRESCRIPTION

        [HttpGet]
        public IActionResult UploadPrescription()
        {
            return View("~/Views/Customer/UploadPrescription.cshtml", new PrescriptionUploadViewModel());
        }

        // UPLOAD PRESCRIPTION (POST & AI SCANNING)
        
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadPrescription(PrescriptionUploadViewModel viewModel)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null || user.CustomerId == null)
            {
                return Unauthorized();
            }

            if (ModelState.IsValid)
            {
                if (viewModel.PrescriptionFile == null || viewModel.PrescriptionFile.Length == 0)
                {
                    ModelState.AddModelError("PrescriptionFile", "Please select a valid prescription file or image.");
                    return View("~/Views/Customer/UploadPrescription.cshtml", viewModel);
                }

                try
                {
                    string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "uploads", "prescriptions");
                    if (!Directory.Exists(uploadsFolder))
                    {
                        Directory.CreateDirectory(uploadsFolder);
                    }

                    string uniqueFileName = Guid.NewGuid().ToString() + "_" + Path.GetFileName(viewModel.PrescriptionFile.FileName);
                    string filePath = Path.Combine(uploadsFolder, uniqueFileName);

                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await viewModel.PrescriptionFile.CopyToAsync(fileStream);
                    }

                    string fileUrl = "/uploads/prescriptions/" + uniqueFileName;

                    // Call Groq AI Service to extract prescription info
                    string extractedText = await _groqService.ExtractMedicineInfoAsync(filePath);

                    var prescription = new Prescription
                    {
                        CustomerId = user.CustomerId.Value,
                        FileUrl = fileUrl,
                        Notes = viewModel.Notes,
                        ExtractedMedicines = extractedText,
                        Status = "Pending",
                        UploadedAt = DateTime.Now
                    };

                    _context.Prescriptions.Add(prescription);
                    await _context.SaveChangesAsync();

                    return RedirectToAction(nameof(MyPrescriptions));
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError("", $"An error occurred during upload: {ex.Message}");
                }
            }

            return View("~/Views/Customer/UploadPrescription.cshtml", viewModel);
        }

        // CHECKOUT
        [HttpGet]
        public async Task<IActionResult> Checkout(int? medicineId, int? quantity)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null || user.CustomerId == null)
            {
                return Unauthorized();
            }

            var cartItems = new List<CartItemViewModel>();

            if (medicineId.HasValue && quantity.HasValue)
            {
                var medicine = await _context.Medicines
                    .Include(m => m.Stocks)
                    .FirstOrDefaultAsync(m => m.MedicineId == medicineId.Value);

                if (medicine != null)
                {
                    int totalStock = medicine.Stocks?.Sum(st => st.Quantity) ?? 0;

                    decimal unitPrice = medicine.Stocks?
                        .OrderByDescending(s => s.CreatedAt)
                        .FirstOrDefault()?.SellingPrice ?? 0.00m;

                    int initialQty = quantity.Value;
                    if (initialQty > totalStock)
                    {
                        initialQty = totalStock > 0 ? totalStock : 1;
                    }
                    if (initialQty < 1)
                    {
                        initialQty = 1;
                    }

                    var singleCartItem = new CartItemViewModel
                    {
                        MedicineId = medicine.MedicineId,
                        MedicineName = medicine.Name ?? "Unknown",
                        UnitPrice = unitPrice,
                        Quantity = initialQty,
                        StockLimit = totalStock
                    };

                    cartItems.Add(singleCartItem);
                }
            }

            var rawPrescriptions = await _context.Prescriptions
                .Where(p => p.CustomerId == user.CustomerId.Value)
                .ToListAsync();

            var prescriptions = new List<SelectListItem>();
            foreach (var p in rawPrescriptions)
            {
                prescriptions.Add(new SelectListItem
                {
                    Value = p.PrescriptionId.ToString(),
                    Text = $"Prescription #{p.PrescriptionId} - Uploaded: {p.UploadedAt:yyyy-MM-dd} ({p.Status})"
                });
            }

            var viewModel = new CheckoutViewModel
            {
                CustomerId = user.CustomerId.Value,
                CartItems = cartItems,
                PaymentMethod = "CashOnDelivery",
                DeliveryMethod = "HomeDelivery",
                Prescriptions = prescriptions
            };

            return View("~/Views/Customer/Checkout.cshtml", viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessCheckout(CheckoutViewModel viewModel)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null || user.CustomerId == null)
            {
                return Unauthorized();
            }

            if (ModelState.IsValid && viewModel.CartItems != null && viewModel.CartItems.Any())
            {
                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    decimal totalAmount = viewModel.CartItems.Sum(item => item.UnitPrice * item.Quantity);

                    var purchase = new Purchase
                    {
                        CustomerId = user.CustomerId.Value,
                        TotalAmount = totalAmount,
                        PaymentMethod = viewModel.PaymentMethod,
                        Status = "Pending",
                        PurchaseDate = DateTime.Now
                    };

                    _context.Purchases.Add(purchase);
                    await _context.SaveChangesAsync();

                    foreach (var item in viewModel.CartItems)
                    {
                        var purchaseItem = new PurchaseItem
                        {
                            PurchaseId = purchase.PurchaseId,
                            MedicineId = item.MedicineId,
                            Quantity = item.Quantity,
                            UnitPrice = item.UnitPrice
                        };
                        _context.PurchaseItems.Add(purchaseItem);

                        var stocks = await _context.Stocks
                            .Where(s => s.MedicineId == item.MedicineId && s.Quantity > 0)
                            .OrderBy(s => s.CreatedAt)
                            .ToListAsync();

                        int remainingQtyToDeduct = item.Quantity;

                        foreach (var stock in stocks)
                        {
                            if (remainingQtyToDeduct <= 0) break;

                            if (stock.Quantity >= remainingQtyToDeduct)
                            {
                                stock.Quantity -= remainingQtyToDeduct;
                                remainingQtyToDeduct = 0;
                            }
                            else
                            {
                                remainingQtyToDeduct -= stock.Quantity;
                                stock.Quantity = 0;
                            }

                            _context.Stocks.Update(stock);
                        }
                    }

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    return RedirectToAction("Index", "Home");
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    ModelState.AddModelError("", "An error occurred while processing your order. Please try again.");
                }
            }

            return View("~/Views/Customer/Checkout.cshtml", viewModel);
        }
    }
}