﻿// <auto-generated />
using System;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace SkyFlipTracker.Migrations
{
    [DbContext(typeof(TrackerDbContext))]
    [Migration("20220327092410_removeTimestamps")]
    partial class removeTimestamps
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "6.0.0")
                .HasAnnotation("Relational:MaxIdentifierLength", 64);

            modelBuilder.Entity("Coflnet.Sky.SkyAuctionTracker.Models.Flip", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    b.Property<long>("AuctionId")
                        .HasColumnType("bigint");

                    b.Property<int>("FinderType")
                        .HasColumnType("int");

                    b.Property<int>("TargetPrice")
                        .HasColumnType("int");

                    b.Property<DateTime>("Timestamp")
                        .HasColumnType("datetime(6)");

                    b.HasKey("Id");

                    b.HasIndex("AuctionId");

                    b.HasIndex("Timestamp");

                    b.ToTable("Flips");
                });

            modelBuilder.Entity("Coflnet.Sky.SkyAuctionTracker.Models.FlipEvent", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    b.Property<long>("AuctionId")
                        .HasColumnType("bigint");

                    b.Property<long>("PlayerId")
                        .HasColumnType("bigint");

                    b.Property<DateTime>("Timestamp")
                        .HasColumnType("datetime(6)");

                    b.Property<int>("Type")
                        .HasColumnType("int");

                    b.HasKey("Id");

                    b.HasIndex("PlayerId");

                    b.HasIndex("Timestamp");

                    b.HasIndex("AuctionId", "Type");

                    b.ToTable("FlipEvents");
                });
#pragma warning restore 612, 618
        }
    }
}
