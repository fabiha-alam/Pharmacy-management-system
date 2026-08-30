using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PharmacyManagement.Data;
using PharmacyManagement.Models.ViewModels.Admin;
using PharmacyManagement.Models.ViewModels.Customer;
using PharmacyManagement.Models.ViewModels.Medicine;
using PharmacyManagement.Models.ViewModels.Prescription;
using PharmacyManagement.Models.ViewModels.Stock;
using PharmacyManagement.Models.ViewModels.Purchase;

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

        [HttpGet]
        public async Task<IActionResult> Dashboard()
        {
            var customerList = await _context.Customers
                .Include(c => c.Purchases)
                    .ThenInclude(p => p.PurchaseItems)
                .ToListAsync();

            var customers = customerList.Select(c => new CustomerListViewModel
            {
                CustomerId = c.CustomerId,
                Name = c.Name,
                Email = c.Email ?? "",
                Phone = c.Phone ?? "",
                Address = c.Address ?? "",
                TotalMedicinesBought = c.Purchases?
                    .SelectMany(p => p.PurchaseItems ?? Enumerable.Empty<Models.PurchaseItem>())
                    .Sum(pi => pi.Quantity) ?? 0
            }).ToList();

            var medicinesList = await _context.Medicines
                .Include(m => m.Stocks)
                .Include(m => m.PurchaseItems)
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
                    MedicineName = m.Name,
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCustomer(int id)
        {
            var customer = await _context.Customers
                .Include(c => c.Purchases)
                .Include(c => c.Prescriptions)
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
                            var filePath = Path.Combine(_webHostEnvironment.WebRootPath, prescription.FileUrl.TrimStart('/'));
                            if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);
                        }
                    }
                    _context.Prescriptions.RemoveRange(customer.Prescriptions);
                }

                _context.Customers.Remove(customer);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Dashboard));
        }

        [HttpGet]
        public async Task<IActionResult> Medicines()
        {
            var medList = await _context.Medicines
                .Include(m => m.Category)
                .Include(m => m.Stocks)
                .ToListAsync();

            var medicines = medList.Select(m => new MedicineViewModel
            {
                MedicineId = m.MedicineId,
                Name = m.Name,
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
        public async Task<IActionResult> Stocks()
        {
            var stockList = await _context.Stocks
                .Include(s => s.Medicine)
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
        public async Task<IActionResult> Prescriptions()
        {
            var presList = await _context.Prescriptions
                .Include(p => p.Customer)
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

                // Determine stock availability status text based on matched items
                string stockStatus = "No Medicines Detected";
                if (matchedItems.Any())
                {
                    bool allAvailable = matchedItems.All(m => m.IsAvailable);
                    bool anyAvailable = matchedItems.Any(m => m.IsAvailable);

                    if (allAvailable)
                    {
                        stockStatus = "All Available";
                    }
                    else if (anyAvailable)
                    {
                        stockStatus = "Partially Available";
                    }
                    else
                    {
                        stockStatus = "Out of Stock";
                    }
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
                if (!string.IsNullOrEmpty(prescription.FileUrl))
                {
                    var filePath = Path.Combine(_webHostEnvironment.WebRootPath, prescription.FileUrl.TrimStart('/'));
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                }

                _context.Prescriptions.Remove(prescription);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Prescriptions));
        }
    }
}