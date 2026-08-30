using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PharmacyManagement.Data;
using PharmacyManagement.Models;
using PharmacyManagement.Models.ViewModels.Admin;
using PharmacyManagement.Models.ViewModels.Medicine;
using PharmacyManagement.Models.ViewModels.Prescription;
using PharmacyManagement.Models.ViewModels.Purchase;
using PharmacyManagement.Models.ViewModels.Stock;
namespace PharmacyManagement.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AdminController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IWebHostEnvironment _webHostEnvironment;

        public AdminController(AppDbContext context, IWebHostEnvironment webHostEnvironment)
        {
            _context = context;
            _webHostEnvironment = webHostEnvironment;
        }

        #region Dashboard & Customer Management

        [HttpGet]
        public async Task<IActionResult> Dashboard()
        {
            try
            {
                var customerList = await _context.Customers
                    .AsNoTracking()
                    .Include(c => c.Purchases!)
                        .ThenInclude(p => p.PurchaseItems!)
                    .ToListAsync();

                var customers = customerList.Select(c => new CustomerListViewModel
                {
                    CustomerId = c.CustomerId,
                    Name = c.Name ?? "Unknown",
                    Email = c.Email ?? "",
                    Phone = c.Phone ?? "",
                    Address = c.Address ?? "",
                    TotalMedicinesBought = (c.Purchases ?? new List<Purchase>())
                        .SelectMany(p => p.PurchaseItems ?? Enumerable.Empty<PurchaseItem>())
                        .Sum(pi => pi.Quantity)
                }).ToList();

                var medicinesList = await _context.Medicines
                    .AsNoTracking()
                    .Include(m => m.Stocks!)
                    .Include(m => m.PurchaseItems!)
                    .ToListAsync();

                var salesStockData = new List<AdminSalesStockViewModel>();
                int totalStockQty = 0;

                foreach (var m in medicinesList)
                {
                    int unitsSold = m.PurchaseItems?.Sum(pi => pi.Quantity) ?? 0;
                    int unitsLeft = m.Stocks?.Sum(s => s.Quantity) ?? 0;
                    totalStockQty += unitsLeft;

                    decimal buyPrice = 0;
                    decimal sellPrice = 0;
                    var latestStock = m.Stocks?.OrderByDescending(s => s.CreatedAt).FirstOrDefault();
                    if (latestStock != null)
                    {
                        buyPrice = latestStock.PurchasePrice;
                        sellPrice = latestStock.SellingPrice;
                    }

                    salesStockData.Add(new AdminSalesStockViewModel
                    {
                        MedicineId = m.MedicineId,
                        MedicineName = m.Name ?? "Unknown",
                        UnitsSold = unitsSold,
                        UnitsLeft = unitsLeft,
                        BuyPrice = buyPrice,
                        SellPrice = sellPrice
                    });
                }

                var viewModel = new AdminDashboardViewModel
                {
                    TotalCustomers = customers.Count,
                    TotalMedicinesStock = totalStockQty,
                    Customers = customers,
                    SalesStocks = salesStockData
                };

                return View(viewModel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in Admin Dashboard: {ex.Message}");
                return StatusCode(500, "Internal Server Error loading dashboard.");
            }
        }

        [HttpGet]
        [Route("Admin/CustomerPurchaseHistory/{id:int}")]
        public async Task<IActionResult> CustomerPurchaseHistory(int id)
        {
            try
            {
                var customer = await _context.Customers
                    .AsNoTracking()
                    .Include(c => c.Purchases!)
                        .ThenInclude(p => p.PurchaseItems!)
                            .ThenInclude(pi => pi.Medicine!)
                    .FirstOrDefaultAsync(c => c.CustomerId == id);

                if (customer == null)
                {
                    return NotFound($"Customer with ID {id} was not found.");
                }
                var purchases = (customer.Purchases ?? new List<Purchase>())
                    .OrderByDescending(p => p.PurchaseDate)
                    .Select(p => new PurchaseOrderViewModel
                    {
                        PurchaseId = p.PurchaseId,
                        PurchaseDate = p.PurchaseDate,
                        TotalAmount = p.TotalAmount,
                        PurchaseItems = (p.PurchaseItems ?? new List<PurchaseItem>())
                            .Select(pi => new PurchaseItemViewModel
                            {
                                PurchaseItemId = pi.PurchaseItemId,
                                MedicineId = pi.MedicineId,
                                MedicineName = pi.Medicine != null ? pi.Medicine.Name : "N/A",
                                Quantity = pi.Quantity,
                                UnitPrice = pi.UnitPrice,
                                TotalPrice = pi.Quantity * pi.UnitPrice
                            }).ToList()
                    }).ToList();

                var viewModel = new CustomerPurchaseHistoryAdminViewModel
                {
                    CustomerId = customer.CustomerId,
                    CustomerName = customer.Name ?? "N/A",
                    Purchases = purchases
                };

                return PartialView("/Views/Admin/CustomerPurchaseHistory.cshtml", viewModel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in CustomerPurchaseHistory: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return StatusCode(500, $"Internal Error: {ex.Message}");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCustomer(int id)
        {
            var customer = await _context.Customers
                .Include(c => c.Purchases!)
                .Include(c => c.Prescriptions!)
                .FirstOrDefaultAsync(c => c.CustomerId == id);

            if (customer != null)
            {
                var user = await _context.Users.FirstOrDefaultAsync(u => u.CustomerId == id);
                if (user != null) _context.Users.Remove(user);

                if (customer.Purchases?.Any() == true)
                {
                    var purchaseIds = customer.Purchases.Select(p => p.PurchaseId).ToList();
                    var purchaseItems = _context.PurchaseItems.Where(pi => purchaseIds.Contains(pi.PurchaseId));
                    _context.PurchaseItems.RemoveRange(purchaseItems);
                    _context.Purchases.RemoveRange(customer.Purchases);
                }

                if (customer.Prescriptions?.Any() == true)
                {
                    foreach (var prescription in customer.Prescriptions)
                    {
                        if (!string.IsNullOrEmpty(prescription.FileUrl))
                        {
                            DeleteFile(prescription.FileUrl);
                        }
                    }
                    _context.Prescriptions.RemoveRange(customer.Prescriptions);
                }

                _context.Customers.Remove(customer);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Dashboard));
        }

        #endregion

        #region Medicine Management

        [HttpGet]
        public async Task<IActionResult> Medicines()
        {
            var medList = await _context.Medicines
                .AsNoTracking()
                .Include(m => m.Category!)
                .Include(m => m.Stocks!)
                .ToListAsync();

            var medicines = medList.Select(m => new MedicineViewModel
            {
                MedicineId = m.MedicineId,
                Name = m.Name ?? "",
                GenericName = m.GenericName ?? "",
                BrandName = m.BrandName ?? "",
                Strength = m.Strength ?? "",
                DosageForm = m.DosageForm ?? "",
                CategoryName = m.Category?.Name ?? "Uncategorized",
                CategoryId = m.CategoryId,
                Manufacturer = m.Manufacturer ?? "",
                ImageUrl = m.ImageUrl ?? "",
                IsPrescriptionRequired = m.IsPrescriptionRequired,
                Description = m.Description ?? "",
                IsActive = m.IsActive,
                TotalStockQuantity = m.Stocks?.Sum(s => s.Quantity) ?? 0,
                LatestSellingPrice = m.Stocks?.OrderByDescending(s => s.CreatedAt).FirstOrDefault()?.SellingPrice ?? 0
            }).ToList();

            return View("~/Views/Admin/Medicine/Index.cshtml", medicines);
        }

        [HttpGet]
        public async Task<IActionResult> Create()
        {
            var viewModel = new MedicineCreateViewModel
            {
                Categories = await PopulateCategoriesSelectList()
            };
            return View("~/Views/Admin/Medicine/Create.cshtml", viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(MedicineCreateViewModel model)
        {
            if (ModelState.IsValid)
            {
                string imagePath = string.Empty;
                if (model.ImageFile != null)
                {
                    imagePath = await UploadFileAsync(model.ImageFile, "uploads/medicines");
                }

                var medicine = new Medicine
                {
                    Name = model.Name,
                    GenericName = model.GenericName,
                    BrandName = model.BrandName,
                    CategoryId = model.CategoryId,
                    DosageForm = model.DosageForm,
                    Strength = model.Strength,
                    Manufacturer = model.Manufacturer,
                    Description = model.Description,
                    IsPrescriptionRequired = model.IsPrescriptionRequired,
                    ImageUrl = imagePath,
                    IsActive = true
                };

                _context.Medicines.Add(medicine);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Medicines));
            }

            model.Categories = await PopulateCategoriesSelectList(model.CategoryId);
            return View("~/Views/Admin/Medicine/Create.cshtml", model);
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var medicine = await _context.Medicines.FindAsync(id);
            if (medicine == null) return NotFound();

            var viewModel = new MedicineEditViewModel
            {
                MedicineId = medicine.MedicineId,
                Name = medicine.Name,
                GenericName = medicine.GenericName,
                BrandName = medicine.BrandName,
                CategoryId = medicine.CategoryId,
                DosageForm = medicine.DosageForm,
                Strength = medicine.Strength,
                Manufacturer = medicine.Manufacturer,
                Description = medicine.Description,
                IsPrescriptionRequired = medicine.IsPrescriptionRequired,
                ImageUrl = medicine.ImageUrl,
                IsActive = medicine.IsActive,
                Categories = await PopulateCategoriesSelectList(medicine.CategoryId)
            };

            return View("~/Views/Admin/Medicine/Edit.cshtml", viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(MedicineEditViewModel model)
        {
            if (ModelState.IsValid)
            {
                var medicine = await _context.Medicines.FindAsync(model.MedicineId);
                if (medicine == null) return NotFound();

                if (model.ImageFile != null)
                {
                    DeleteFile(medicine.ImageUrl);
                    medicine.ImageUrl = await UploadFileAsync(model.ImageFile, "uploads/medicines");
                }

                medicine.Name = model.Name;
                medicine.GenericName = model.GenericName;
                medicine.BrandName = model.BrandName;
                medicine.CategoryId = model.CategoryId;
                medicine.DosageForm = model.DosageForm;
                medicine.Strength = model.Strength;
                medicine.Manufacturer = model.Manufacturer;
                medicine.Description = model.Description;
                medicine.IsPrescriptionRequired = model.IsPrescriptionRequired;
                medicine.IsActive = model.IsActive;

                _context.Medicines.Update(medicine);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Medicines));
            }

            model.Categories = await PopulateCategoriesSelectList(model.CategoryId);
            return View("~/Views/Admin/Medicine/Edit.cshtml", model);
        }

        [HttpGet]
        public async Task<IActionResult> DeleteMedicine(int id)
        {
            var medicine = await _context.Medicines
                .AsNoTracking()
                .Include(m => m.Category!)
                .FirstOrDefaultAsync(m => m.MedicineId == id);

            if (medicine == null) return NotFound();

            return View("~/Views/Admin/Medicine/Delete.cshtml", medicine);
        }

        [HttpPost, ActionName("DeleteMedicine")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMedicineConfirmed(int medicineId)
        {
            var medicine = await _context.Medicines
                .Include(m => m.Stocks!)
                .FirstOrDefaultAsync(m => m.MedicineId == medicineId);

            if (medicine != null)
            {
                DeleteFile(medicine.ImageUrl);

                if (medicine.Stocks != null && medicine.Stocks.Any())
                {
                    _context.Stocks.RemoveRange(medicine.Stocks);
                }

                _context.Medicines.Remove(medicine);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Medicines));
        }

        #endregion

        #region Stock Management

        [HttpGet]
        public async Task<IActionResult> Stocks()
        {
            var stockList = await _context.Stocks
                .AsNoTracking()
                .Include(s => s.Medicine!)
                .ToListAsync();

            var stocks = stockList.Select(s => new StockViewModel
            {
                StockId = s.StockId,
                MedicineId = s.MedicineId,
                MedicineName = s.Medicine?.Name ?? "Unknown",
                BatchNumber = s.BatchNumber ?? "",
                Quantity = s.Quantity,
                PurchasePrice = s.PurchasePrice,
                SellingPrice = s.SellingPrice,
                ExpiryDate = s.ExpiryDate,
                CreatedAt = s.CreatedAt
            }).ToList();

            return View("~/Views/Admin/Stock/Index.cshtml", stocks);
        }

        [HttpGet]
        public async Task<IActionResult> AddStock()
        {
            var viewModel = new StockCreateEditViewModel
            {
                Medicines = await PopulateMedicinesSelectList()
            };
            return View("~/Views/Admin/Stock/AddStock.cshtml", viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddStock(StockCreateEditViewModel model)
        {
            if (ModelState.IsValid)
            {
                var stock = new Stock
                {
                    MedicineId = model.MedicineId,
                    BatchNumber = model.BatchNumber,
                    Quantity = model.Quantity,
                    PurchasePrice = model.PurchasePrice,
                    SellingPrice = model.SellingPrice,
                    ExpiryDate = model.ExpiryDate,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Stocks.Add(stock);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Stocks));
            }

            model.Medicines = await PopulateMedicinesSelectList(model.MedicineId);
            return View("~/Views/Admin/Stock/AddStock.cshtml", model);
        }

        [HttpGet]
        public async Task<IActionResult> EditStock(int id)
        {
            var stock = await _context.Stocks.FindAsync(id);
            if (stock == null) return NotFound();

            var viewModel = new StockCreateEditViewModel
            {
                StockId = stock.StockId,
                MedicineId = stock.MedicineId,
                BatchNumber = stock.BatchNumber,
                Quantity = stock.Quantity,
                PurchasePrice = stock.PurchasePrice,
                SellingPrice = stock.SellingPrice,
                ExpiryDate = stock.ExpiryDate,
                Medicines = await PopulateMedicinesSelectList(stock.MedicineId)
            };

            return View("~/Views/Admin/Stock/Edit.cshtml", viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditStock(StockCreateEditViewModel model)
        {
            if (ModelState.IsValid)
            {
                var stock = await _context.Stocks.FindAsync(model.StockId);
                if (stock == null) return NotFound();

                stock.MedicineId = model.MedicineId;
                stock.BatchNumber = model.BatchNumber;
                stock.Quantity = model.Quantity;
                stock.PurchasePrice = model.PurchasePrice;
                stock.SellingPrice = model.SellingPrice;
                stock.ExpiryDate = model.ExpiryDate;

                _context.Stocks.Update(stock);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Stocks));
            }

            model.Medicines = await PopulateMedicinesSelectList(model.MedicineId);
            return View("~/Views/Admin/Stock/Edit.cshtml", model);
        }

        [HttpGet]
        public async Task<IActionResult> DeleteStock(int id)
        {
            var stock = await _context.Stocks
                .AsNoTracking()
                .Include(s => s.Medicine!)
                .FirstOrDefaultAsync(s => s.StockId == id);

            if (stock == null) return NotFound();

            return View("~/Views/Admin/Stock/Delete.cshtml", stock);
        }

        [HttpPost, ActionName("DeleteStock")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteStockConfirmed(int stockId)
        {
            var stock = await _context.Stocks.FindAsync(stockId);
            if (stock != null)
            {
                _context.Stocks.Remove(stock);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Stocks));
        }

        #endregion

        #region Prescription Management

        [HttpGet]
        public async Task<IActionResult> Prescriptions()
        {
            var presList = await _context.Prescriptions
                .AsNoTracking()
                .Include(p => p.Customer!)
                .OrderByDescending(p => p.UploadedAt)
                .ToListAsync();

            var allMedicines = await _context.Medicines
                .AsNoTracking()
                .Include(m => m.Stocks!)
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

                string stockStatus = "No Medicines Detected";
                if (matchedItems.Any())
                {
                    bool allAvailable = matchedItems.All(m => m.IsAvailable);
                    bool anyAvailable = matchedItems.Any(m => m.IsAvailable);

                    if (allAvailable) stockStatus = "All Available";
                    else if (anyAvailable) stockStatus = "Partially Available";
                    else stockStatus = "Out of Stock";
                }

                prescriptions.Add(new PrescriptionViewModel
                {
                    PrescriptionId = p.PrescriptionId,
                    CustomerId = p.CustomerId,
                    CustomerName = p.Customer?.Name ?? "Unknown Customer",
                    FileUrl = p.FileUrl ?? string.Empty,
                    Notes = p.Notes,
                    ExtractedMedicines = p.ExtractedMedicines,
                    Status = stockStatus,
                    UploadedAt = p.UploadedAt,
                    MatchedItems = matchedItems
                });
            }

            return View("~/Views/Admin/Prescriptions.cshtml", prescriptions);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePrescription(int id)
        {
            var prescription = await _context.Prescriptions.FindAsync(id);

            if (prescription != null)
            {
                DeleteFile(prescription.FileUrl);
                _context.Prescriptions.Remove(prescription);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Prescriptions));
        }

        #endregion

        #region Helper Methods

        private async Task<IEnumerable<SelectListItem>> PopulateCategoriesSelectList(int? selectedId = null)
        {
            var categories = await _context.Categories.AsNoTracking().ToListAsync();
            return categories.Select(c => new SelectListItem
            {
                Value = c.CategoryId.ToString(),
                Text = c.Name,
                Selected = selectedId.HasValue && c.CategoryId == selectedId.Value
            });
        }

        private async Task<IEnumerable<SelectListItem>> PopulateMedicinesSelectList(int? selectedId = null)
        {
            var medicines = await _context.Medicines.AsNoTracking().Where(m => m.IsActive).ToListAsync();
            return medicines.Select(m => new SelectListItem
            {
                Value = m.MedicineId.ToString(),
                Text = m.Name,
                Selected = selectedId.HasValue && m.MedicineId == selectedId.Value
            });
        }

        private async Task<string> UploadFileAsync(IFormFile file, string folderPath)
        {
            string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, folderPath);
            if (!Directory.Exists(uploadsFolder))
            {
                Directory.CreateDirectory(uploadsFolder);
            }

            string uniqueFileName = $"{Guid.NewGuid()}_{Path.GetFileName(file.FileName)}";
            string filePath = Path.Combine(uploadsFolder, uniqueFileName);

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(fileStream);
            }

            return $"/{folderPath}/{uniqueFileName}";
        }

        private void DeleteFile(string? relativeUrl)
        {
            if (string.IsNullOrEmpty(relativeUrl)) return;

            var filePath = Path.Combine(_webHostEnvironment.WebRootPath, relativeUrl.TrimStart('/'));
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }
        }

        #endregion
    }
}