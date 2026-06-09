using CryptoEngine.Interfaces;
using CryptoEngine.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace CryptoEngine
{
    public static class DependencyInjection 
    {
        public static IServiceCollection AddCryptoEngine(this IServiceCollection services)
        {
            services.AddScoped<IArgonKeyDerivationService, ArgonKeyDerivationService>();
            services.AddScoped<ISymmetricService, AesGcmSymmetricService>();
            services.AddScoped<IKeyPairService, KeyPairService>();
            services.AddScoped<IHkdfService, HkdfService>();
            services.AddScoped<IIdentityFingerprintService, IdentityFingerprintService>();
            services.AddScoped<IX3dhService, X3dhService>();
            services.AddScoped<IX3dhHeaderSerializer, X3dhHeaderJsonSerializer>();
            services.AddScoped<IFileKeyDerivationService, FileKeyDerivationService>();
            services.AddScoped<IFileMetadataProtector, FileMetadataProtector>();
            services.AddScoped<IFileContentEncryptionService, FileContentEncryptionService>();

            return services;
        }
    }
}
