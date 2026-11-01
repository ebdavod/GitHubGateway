using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using GitHubGateway.Application.DTOs;

namespace GitHubGateway.Application.Validators
{
    public class UpdateFileRequestValidator : AbstractValidator<UpdateFileRequest>
    {
        public UpdateFileRequestValidator()
        {
            RuleFor(x => x.Owner).NotEmpty().MaximumLength(100)
                .Matches("^[A-Za-z0-9._-]+$").WithMessage("Owner contains invalid characters.");
            RuleFor(x => x.Repo).NotEmpty().MaximumLength(100)
                .Matches("^[A-Za-z0-9._-]+$").WithMessage("Repo contains invalid characters.");
            RuleFor(x => x.Path).NotEmpty().MaximumLength(1024);
            RuleFor(x => x.Branch).NotEmpty().MaximumLength(255);
            RuleFor(x => x.CommitMessage).NotEmpty().MaximumLength(500);
            RuleFor(x => x.Content).NotNull();
            // Note: we deliberately allow content to be empty (file truncation use-case).
        }
    }

    public class DeleteFileRequestValidator : AbstractValidator<DeleteFileRequest>
    {
        public DeleteFileRequestValidator()
        {
            RuleFor(x => x.Owner).NotEmpty().MaximumLength(100);
            RuleFor(x => x.Repo).NotEmpty().MaximumLength(100);
            RuleFor(x => x.Path).NotEmpty().MaximumLength(1024);
            RuleFor(x => x.Sha).NotEmpty().Length(40);
            RuleFor(x => x.CommitMessage).NotEmpty().MaximumLength(500);
        }
    }

    public class CreateBranchRequestValidator : AbstractValidator<CreateBranchRequest>
    {
        public CreateBranchRequestValidator()
        {
            RuleFor(x => x.Owner).NotEmpty().MaximumLength(100);
            RuleFor(x => x.Repo).NotEmpty().MaximumLength(100);
            RuleFor(x => x.NewBranchName).NotEmpty().MaximumLength(255)
                .Matches("^[A-Za-z0-9._/-]+$").WithMessage("Branch name contains invalid characters.");
            RuleFor(x => x.FromBranch).NotEmpty().MaximumLength(255);
        }
    }

    public class CreatePullRequestRequestValidator : AbstractValidator<CreatePullRequestRequest>
    {
        public CreatePullRequestRequestValidator()
        {
            RuleFor(x => x.Owner).NotEmpty().MaximumLength(100);
            RuleFor(x => x.Repo).NotEmpty().MaximumLength(100);
            RuleFor(x => x.Title).NotEmpty().MaximumLength(256);
            RuleFor(x => x.Head).NotEmpty().MaximumLength(255);
            RuleFor(x => x.Base).NotEmpty().MaximumLength(255);
            RuleFor(x => x.Body).MaximumLength(65000);
        }
    }

    public class ApplyPatchRequestValidator : AbstractValidator<ApplyPatchRequest>
    {
        public ApplyPatchRequestValidator()
        {
            RuleFor(x => x.Owner).NotEmpty().MaximumLength(100);
            RuleFor(x => x.Repo).NotEmpty().MaximumLength(100);
            RuleFor(x => x.Branch).NotEmpty().MaximumLength(255);
            RuleFor(x => x.PatchText).NotEmpty().MaximumLength(2_000_000);
            RuleFor(x => x.CommitMessage).NotEmpty().MaximumLength(500);
        }
    }
}
