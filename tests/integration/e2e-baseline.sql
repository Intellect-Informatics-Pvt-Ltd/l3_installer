-- The end-to-end's schema: small enough that the census is checkable by eye. The real medium
-- carries db/stable_baseline_ddl.sql from the L2-R2 workspace (1,255 tables).
CREATE TABLE `fa_vouchermain` (
  `VoucherId` INT NOT NULL AUTO_INCREMENT,
  `PacsId` VARCHAR(20) NOT NULL,
  `VoucherDate` DATE NOT NULL,
  `Amount` DECIMAL(13,2) NOT NULL,
  PRIMARY KEY (`VoucherId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `mem_member` (
  `MemberId` INT NOT NULL AUTO_INCREMENT,
  `PacsId` VARCHAR(20) NOT NULL,
  `Name` VARCHAR(100) NOT NULL,
  PRIMARY KEY (`MemberId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
